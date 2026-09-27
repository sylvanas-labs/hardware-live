using HardwareLive.Core;
using HardwareLive.Core.Analysis;
using HardwareLive.Core.Classification;
using HardwareLive.Core.Profiles;
using HardwareLive.Protocol;

namespace HardwareLive.Tests.Analysis;

public sealed class HealthAnalyzerTests
{
    private const string CpuTempId = "/cpu/temp";
    private const string CpuLoadId = "/cpu/load";
    private const string CpuClockId = "/cpu/clock";
    private const string GpuLoadId = "/gpu/load";
    private const string GpuClockId = "/gpu/clock";
    private const string GpuTempId = "/gpu/temp";
    private const string GpuPowerPctId = "/gpu/power/pct";

    // ---- Rule 1: generic temperature levels -------------------------------------------

    [Fact]
    public void GenericTempTriggersWatchThenCriticalAndIsSilentBelowWatch()
    {
        var role = Role("/board/vrm", Roles.BoardTempVrm, "/board");
        var classification = Classify(role);
        var thresholds = Thresholds((role.SensorId, 85, 100));

        var healthy = HealthAnalyzer.Analyze(Snapshot((role.SensorId, [50f])), classification, thresholds, false);
        Assert.DoesNotContain(healthy.Concerns, c => c.SensorId == role.SensorId);
        Assert.Equal(HealthStatus.HEALTHY, healthy.Status);

        var watch = HealthAnalyzer.Analyze(Snapshot((role.SensorId, [90f])), classification, thresholds, false);
        Assert.Contains(watch.Concerns, c => c.SensorId == role.SensorId && c.Level == ConcernLevel.Watch);
        Assert.Equal(HealthStatus.WATCH, watch.Status);

        var critical = HealthAnalyzer.Analyze(Snapshot((role.SensorId, [101f])), classification, thresholds, false);
        Assert.Contains(critical.Concerns, c => c.SensorId == role.SensorId && c.Level == ConcernLevel.Critical);
        Assert.Equal(HealthStatus.CRITICAL, critical.Status);
    }

    [Fact]
    public void NullNaNAndAbsentSensorsAreSkippedNeverThrow()
    {
        var present = Role("/board/vrm", Roles.BoardTempVrm, "/board");
        var absent = Role("/missing", Roles.DimmTemp, "/dimm");
        var classification = Classify(present, absent);
        var thresholds = Thresholds((present.SensorId, 85, 100), (absent.SensorId, 60, 80));

        var result = HealthAnalyzer.Analyze(
            Snapshot((present.SensorId, [null, float.NaN])),
            classification,
            thresholds,
            false);

        Assert.Equal(HealthStatus.HEALTHY, result.Status);
        Assert.Empty(result.Concerns);
    }

    [Fact]
    public void MessagesNeverEmbedRawHardwareNames()
    {
        const string garbledDimmName = "G Skill Intl - F5-6000J3636F32G\r\u0000\u0000\u0000\u0000\u001aA\u001aA\u001aA\u0000} (#3)";
        var role = Role("/memory/dimm/3/temperature/0", Roles.DimmTemp, "/memory/dimm/3");
        var classification = Classify(role);
        var thresholds = Thresholds((role.SensorId, 60, 80));

        var result = HealthAnalyzer.Analyze(Snapshot((role.SensorId, [90f])), classification, thresholds, false);

        var concern = Assert.Single(result.Concerns);
        Assert.DoesNotContain(garbledDimmName, concern.Message, StringComparison.Ordinal);
        Assert.Contains("RAM temp", concern.Message, StringComparison.Ordinal);
    }

    // ---- Rule 2: CPU Tjmax-by-design regression -----------------------------------------

    [Fact]
    public void CpuControlAtLimitWithNormalClockIsWatchNotCritical()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(95.6f, 30)),
            (CpuLoadId, Repeat(100f, 30)),
            (CpuClockId, ClockRampThenDip(5200f, 4879f, 30)));

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        var concern = Assert.Single(result.Concerns, c => c.SensorId == CpuTempId);
        Assert.Equal(ConcernLevel.Watch, concern.Level);
        Assert.Equal("at thermal limit, boost reduced - normal for this CPU under full load", concern.Message);
        Assert.Equal(HealthStatus.WATCH, result.Status);
    }

    [Fact]
    public void CpuControlWellOverLimitIsCriticalRegardlessOfClock()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(97.8f, 30)),
            (CpuLoadId, Repeat(100f, 30)),
            (CpuClockId, ClockRampThenDip(5200f, 4879f, 30)));

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        var concern = Assert.Single(result.Concerns, c => c.SensorId == CpuTempId);
        Assert.Equal(ConcernLevel.Critical, concern.Level);
        Assert.Equal(HealthStatus.CRITICAL, result.Status);
    }

    [Fact]
    public void CpuControlAtLimitWithClockCollapseIsCritical()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(95.6f, 30)),
            (CpuLoadId, Repeat(100f, 30)),
            (CpuClockId, ClockRampThenDip(5200f, 2500f, 30)));

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        var concern = Assert.Single(result.Concerns, c => c.SensorId == CpuTempId);
        Assert.Equal(ConcernLevel.Critical, concern.Level);
    }

    [Fact]
    public void CpuControlBetweenWatchAndCriticalIsPlainWatch()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(90f, 30)),
            (CpuLoadId, Repeat(20f, 30)),
            (CpuClockId, Repeat(5200f, 30)));

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        var concern = Assert.Single(result.Concerns, c => c.SensorId == CpuTempId);
        Assert.Equal(ConcernLevel.Watch, concern.Level);
        Assert.DoesNotContain("normal for this CPU", concern.Message, StringComparison.Ordinal);
    }

    // ---- Rule 3: trends ------------------------------------------------------------------

    [Fact]
    public void RisingTrendNearWatchWithLowEtaRaisesConcern()
    {
        var role = Role(CpuTempId, Roles.CpuTempControl, "/cpu/0");
        var classification = Classify(role);
        var thresholds = Thresholds((CpuTempId, 88, 95));
        var slopePerSample = 2.0 / 60.0; // 2 C/min
        var history = Linear(90f, slopePerSample, 30); // ends at 90 C

        var result = HealthAnalyzer.Analyze(Snapshot((CpuTempId, history)), classification, thresholds, false);

        var trend = Assert.Single(result.Trends);
        Assert.InRange(trend.SlopePerMin, 1.9, 2.1);
        Assert.NotNull(trend.EtaMinutes);
        Assert.Contains(result.Concerns, c => c.SensorId == CpuTempId && c.Message.Contains("on track", StringComparison.Ordinal));
    }

    [Fact]
    public void RisingTrendFarFromWatchIsReportedButRaisesNoConcern()
    {
        var role = Role(CpuTempId, Roles.CpuTempControl, "/cpu/0");
        var classification = Classify(role);
        var thresholds = Thresholds((CpuTempId, 88, 95));
        var history = Linear(50f, 1.0 / 60.0, 30); // 1 C/min, far below watch-5

        var result = HealthAnalyzer.Analyze(Snapshot((CpuTempId, history)), classification, thresholds, false);

        Assert.Single(result.Trends);
        Assert.DoesNotContain(result.Concerns, c => c.Message.Contains("on track", StringComparison.Ordinal));
    }

    [Fact]
    public void FlatHistoryHasNoTrend()
    {
        var role = Role(CpuTempId, Roles.CpuTempControl, "/cpu/0");
        var classification = Classify(role);
        var thresholds = Thresholds((CpuTempId, 88, 95));

        var result = HealthAnalyzer.Analyze(Snapshot((CpuTempId, Repeat(60f, 30))), classification, thresholds, false);

        Assert.Empty(result.Trends);
    }

    [Fact]
    public void FewerThanTwentySamplesNeverProducesATrend()
    {
        var role = Role(CpuTempId, Roles.CpuTempControl, "/cpu/0");
        var classification = Classify(role);
        var thresholds = Thresholds((CpuTempId, 88, 95));
        var history = Linear(50f, 5.0 / 60.0, 10);

        var result = HealthAnalyzer.Analyze(Snapshot((CpuTempId, history)), classification, thresholds, false);

        Assert.Empty(result.Trends);
    }

    // ---- Rule 4: throttle -----------------------------------------------------------------

    [Fact]
    public void CpuThrottleTriggersWhenClockDropsBelow85PercentOfSessionMaxUnderLoad()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(60f, 30)),
            (CpuLoadId, Repeat(95f, 30)),
            (CpuClockId, ClockRampThenDip(5000f, 4000f, 30))); // 4000/5000 = 80% < 85%

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        Assert.Contains(result.Concerns, c => c.SensorId == CpuClockId && c.Message == "boost reduced");
    }

    [Fact]
    public void CpuThrottleDoesNotTriggerAboveEightyFivePercent()
    {
        var (classification, thresholds) = CpuScenario();
        var snapshot = Snapshot(
            (CpuTempId, Repeat(60f, 30)),
            (CpuLoadId, Repeat(95f, 30)),
            (CpuClockId, ClockRampThenDip(5000f, 4600f, 30))); // 92%, above the 85% gate

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        Assert.DoesNotContain(result.Concerns, c => c.SensorId == CpuClockId);
    }

    [Fact]
    public void CpuThrottleNeedsAtLeastThirtySecondsUnderLoad()
    {
        var (classification, thresholds) = CpuScenario();
        var loadHistory = new float?[] { 95f, 95f, 95f, 95f, 95f }; // only 5 samples under load
        var clockHistory = new float?[] { 5000f, 5000f, 5000f, 5000f, 2000f };
        var snapshot = Snapshot(
            (CpuTempId, Repeat(60f, 5)),
            (CpuLoadId, loadHistory),
            (CpuClockId, clockHistory));

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        Assert.DoesNotContain(result.Concerns, c => c.SensorId == CpuClockId);
    }

    [Fact]
    public void GpuThrottleTriggersBelowSeventyPercent()
    {
        var role = Role(GpuTempId, Roles.GpuTempCore, "/gpu/0");
        var loadRole = Role(GpuLoadId, Roles.GpuLoadCore, "/gpu/0");
        var clockRole = Role(GpuClockId, Roles.GpuClockCore, "/gpu/0");
        var classification = Classify(role, loadRole, clockRole);
        var thresholds = Thresholds((GpuTempId, 83, 90));
        var snapshot = Snapshot(
            (GpuTempId, Repeat(60f, 30)),
            (GpuLoadId, Repeat(97f, 30)),
            (GpuClockId, ClockRampThenDip(2500f, 1600f, 30))); // 64%

        var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, false);

        Assert.Contains(result.Concerns, c => c.SensorId == GpuClockId);
    }

    // ---- Rule 5: power limit (informational) ---------------------------------------------

    [Fact]
    public void PowerLimitedForThirtySamplesRaisesInfoWithoutAffectingStatus()
    {
        var role = Role(GpuPowerPctId, Roles.GpuPowerPct, "/gpu/0");
        var classification = Classify(role);
        var result = HealthAnalyzer.Analyze(Snapshot((GpuPowerPctId, Repeat(99f, 30))), classification, ThresholdResolution.Empty, false);

        var concern = Assert.Single(result.Concerns);
        Assert.Equal(ConcernLevel.Info, concern.Level);
        Assert.Equal("power-limited", concern.Message);
        Assert.Equal(HealthStatus.HEALTHY, result.Status);
    }

    [Fact]
    public void PowerLimitedForFewerThanThirtySamplesDoesNotTrigger()
    {
        var role = Role(GpuPowerPctId, Roles.GpuPowerPct, "/gpu/0");
        var classification = Classify(role);
        var result = HealthAnalyzer.Analyze(Snapshot((GpuPowerPctId, Repeat(99f, 29))), classification, ThresholdResolution.Empty, false);

        Assert.Empty(result.Concerns);
    }

    // ---- Rule 6: fan/pump stall -----------------------------------------------------------

    [Fact]
    public void CpuFanStoppingForSixSecondsIsCritical()
    {
        const string fanId = "/fan/cpu";
        var role = Role(fanId, Roles.FanCpu, "/board");
        var classification = Classify(role);
        var history = Repeat(1500f, 24).Concat(Enumerable.Repeat<float?>(0f, 6)).ToArray();

        var result = HealthAnalyzer.Analyze(Snapshot((fanId, history)), classification, ThresholdResolution.Empty, false);

        var concern = Assert.Single(result.Concerns);
        Assert.Equal(ConcernLevel.Critical, concern.Level);
        Assert.Equal(HealthStatus.CRITICAL, result.Status);
    }

    [Fact]
    public void FanNeverAboveThreshHoldIsIgnored()
    {
        const string fanId = "/fan/system1";
        var role = Role(fanId, Roles.FanSystem, "/board");
        var classification = Classify(role);
        var history = Enumerable.Repeat<float?>(0f, 30).ToArray();

        var result = HealthAnalyzer.Analyze(Snapshot((fanId, history)), classification, ThresholdResolution.Empty, false);

        Assert.Empty(result.Concerns);
    }

    [Fact]
    public void GpuFanZeroWithCoolGpuIsZeroRpmModeNotAStall()
    {
        const string fanId = "/fan/gpu1";
        var fanRole = Role(fanId, Roles.GpuFan, "/gpu/0");
        var tempRole = Role(GpuTempId, Roles.GpuTempCore, "/gpu/0");
        var classification = Classify(fanRole, tempRole);
        var fanHistory = Repeat(1200f, 24).Concat(Enumerable.Repeat<float?>(0f, 6)).ToArray();

        var result = HealthAnalyzer.Analyze(
            Snapshot((fanId, fanHistory), (GpuTempId, Repeat(40f, 30))),
            classification,
            ThresholdResolution.Empty,
            false);

        Assert.Empty(result.Concerns);
    }

    [Fact]
    public void FanStillSpinningIsNotAStall()
    {
        const string fanId = "/fan/cpu";
        var role = Role(fanId, Roles.FanCpu, "/board");
        var classification = Classify(role);

        var result = HealthAnalyzer.Analyze(Snapshot((fanId, Repeat(1500f, 30))), classification, ThresholdResolution.Empty, false);

        Assert.Empty(result.Concerns);
    }

    // ---- Rule 7: load phase ---------------------------------------------------------------

    [Theory]
    [InlineData(5f, 5f, LoadPhase.Idle)]
    [InlineData(90f, 20f, LoadPhase.CpuBound)]
    [InlineData(30f, 90f, LoadPhase.GpuBound)]
    [InlineData(70f, 70f, LoadPhase.Combined)]
    public void LoadPhaseIsDerivedFromThirtySecondAverages(float cpu, float gpu, string expectedPhase)
    {
        var cpuRole = Role(CpuLoadId, Roles.CpuLoadTotal, "/cpu/0");
        var gpuRole = Role(GpuLoadId, Roles.GpuLoadCore, "/gpu/0");
        var classification = Classify(cpuRole, gpuRole);

        var result = HealthAnalyzer.Analyze(
            Snapshot((CpuLoadId, Repeat(cpu, 30)), (GpuLoadId, Repeat(gpu, 30))),
            classification,
            ThresholdResolution.Empty,
            false);

        Assert.Equal(expectedPhase, result.Phase);
    }

    // ---- Config-invalid concern -------------------------------------------------------

    [Fact]
    public void InvalidConfigFlagRaisesInfoConcern()
    {
        var result = HealthAnalyzer.Analyze(Snapshot(), ClassificationResult.Empty, ThresholdResolution.Empty, true);

        var concern = Assert.Single(result.Concerns);
        Assert.Equal(ConcernLevel.Info, concern.Level);
        Assert.Equal("config.json invalid, using defaults", concern.Message);
        Assert.Equal(HealthStatus.HEALTHY, result.Status);
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static (ClassificationResult Classification, ThresholdResolution Thresholds) CpuScenario()
    {
        var tempRole = Role(CpuTempId, Roles.CpuTempControl, "/cpu/0");
        var loadRole = Role(CpuLoadId, Roles.CpuLoadTotal, "/cpu/0");
        var clockRole = Role(CpuClockId, Roles.CpuClockEffectiveAvg, "/cpu/0");
        var classification = Classify(tempRole, loadRole, clockRole);
        var thresholds = Thresholds((CpuTempId, 88, 95));
        return (classification, thresholds);
    }

    private static SensorRole Role(string sensorId, string role, string hardwareId) =>
        new(sensorId, role, hardwareId, Confidence.High);

    private static ClassificationResult Classify(params SensorRole[] roles) =>
        new(roles, [], "/cpu/0", "/gpu/0", []);

    private static ThresholdResolution Thresholds(params (string SensorId, double Watch, double Critical)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.SensorId,
            e => new ThresholdEntry(e.Watch, e.Critical, "community", ThresholdConfidence.Community, ThresholdOrigin.Generic),
            StringComparer.Ordinal);
        return new ThresholdResolution(dict, null, null);
    }

    private static TelemetrySnapshot Snapshot(params (string Id, float?[] History)[] histories)
    {
        var dict = histories.ToDictionary(
            h => h.Id,
            h => (IReadOnlyList<float?>)h.History,
            StringComparer.Ordinal);
        return new TelemetrySnapshot(null, false, dict);
    }

    private static float?[] Repeat(float value, int count) => Enumerable.Repeat((float?)value, count).ToArray();

    /// <summary>All samples at <paramref name="baseline"/> except the last, which is
    /// <paramref name="lastValue"/> -- models a session max established over sustained load
    /// with a just-now clock dip/collapse on the latest sample.</summary>
    private static float?[] ClockRampThenDip(float baseline, float lastValue, int count)
    {
        var values = new float?[count];
        for (var i = 0; i < count - 1; i++)
        {
            values[i] = baseline;
        }

        values[count - 1] = lastValue;
        return values;
    }

    private static float?[] Linear(float endValue, double slopePerSample, int count)
    {
        var values = new float?[count];
        var start = endValue - (slopePerSample * (count - 1));
        for (var i = 0; i < count; i++)
        {
            values[i] = (float)(start + (slopePerSample * i));
        }

        return values;
    }
}
