using System.Globalization;
using HardwareLive.Core.Classification;
using HardwareLive.Core.Profiles;

namespace HardwareLive.Core.Analysis;

/// <summary>
/// The Component 5 rule-based health analyzer: a pure function over a
/// <see cref="TelemetrySnapshot"/>'s history, the current classification, and resolved
/// thresholds (docs/SPEC.md "Analysis rules"). The caller (RequestRouter) is responsible for
/// the sampler-state short-circuits (not running / stale / identity mismatch / missing
/// mandatory roles) that yield UNKNOWN before this ever runs; by the time <see cref="Analyze"/>
/// is called, a fresh, well-identified frame with every mandatory role is guaranteed.
///
/// Sample cadence is assumed to be 1 Hz (Component 1: the sampler polls at 1 Hz), so every
/// "N seconds" window in the spec is just the last N history entries, and a slope in
/// units/sample is converted to units/minute by multiplying by 60. History carries no
/// per-sample timestamps, so this is a documented assumption, not a measured rate.
/// </summary>
public static class HealthAnalyzer
{
    private const int SamplesPerMinute = 60;
    private const int TrendWindow = 180;
    // 60 non-null samples = 1 minute at the sampler's 1 Hz cadence (see class remarks): a
    // ~20-sample window let a bursty CPU produce an alarming slope ("47.7 F/min") seconds
    // after startup, before there was enough signal to trust a least-squares fit.
    private const int MinTrendSamples = 60;
    private const string LevelOk = "ok";
    private const double TrendSlopeThreshold = 0.4;
    private const double TrendEtaMinutesGate = 10;
    private const double TrendValueGateOffset = 5;
    private const int UnderLoadMinSamples = 30;
    private const int FanStallSamples = 5;
    private const int FanConnectedWindow = 300;
    private const int PowerLimitedSamples = 30;

    private static readonly HashSet<string> FanStallRoles = new(StringComparer.Ordinal)
    {
        Roles.FanCpu, Roles.FanCpuOpt, Roles.FanPump, Roles.FanSystem, Roles.GpuFan,
    };

    private static readonly HashSet<string> CriticalFanRoles = new(StringComparer.Ordinal)
    {
        Roles.FanCpu, Roles.FanCpuOpt, Roles.FanPump,
    };

    public static AnalysisResult Analyze(
        TelemetrySnapshot snapshot,
        ClassificationResult classification,
        ThresholdResolution thresholds,
        bool configInvalid)
    {
        var concerns = new List<Concern>();
        var trends = new List<Trend>();
        var sensorLevels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (configInvalid)
        {
            concerns.Add(new Concern(ConcernLevel.Info, "config", "config.json", "config.json invalid, using defaults"));
        }

        EvaluateTemperatures(snapshot, classification, thresholds, concerns, sensorLevels);
        EvaluateTrends(snapshot, classification, thresholds, concerns, trends);
        EvaluateThrottle(snapshot, classification, concerns);
        EvaluatePowerLimit(snapshot, classification, concerns);
        EvaluateFanStall(snapshot, classification, concerns);

        var phase = ComputeLoadPhase(snapshot, classification);
        var status = concerns.Any(c => c.Level == ConcernLevel.Critical) ? HealthStatus.CRITICAL
            : concerns.Any(c => c.Level == ConcernLevel.Watch) ? HealthStatus.WATCH
            : HealthStatus.HEALTHY;
        var headline = BuildHeadline(status, phase, snapshot, classification);

        return new AnalysisResult(status, null, headline, phase, concerns, trends, sensorLevels);
    }

    // ---- Rules 1 & 2: temperature levels, with the CPU Tjmax-by-design carve-out --------

    private static void EvaluateTemperatures(
        TelemetrySnapshot snapshot,
        ClassificationResult classification,
        ThresholdResolution thresholds,
        List<Concern> concerns,
        Dictionary<string, string> sensorLevels)
    {
        foreach (var role in classification.Roles)
        {
            if (!thresholds.Thresholds.TryGetValue(role.SensorId, out var threshold))
            {
                continue;
            }

            var value = LatestValue(GetTemperatureHistory(snapshot, classification, role));
            if (value is not { } v || !float.IsFinite(v))
            {
                continue;
            }

            var level = role.Role == Roles.CpuTempControl
                ? EvaluateCpuControlTemp(role, v, threshold, snapshot, classification, concerns)
                : EvaluateGenericTemp(role, v, threshold, concerns);
            sensorLevels[role.SensorId] = level;
        }
    }

    /// <summary>Finding 4: the AMD CCD fallback (<see cref="ClassificationResult.CpuControlIsCcdMax"/>)
    /// freezes which single CCD sensor id carries the <see cref="Roles.CpuTempControl"/>
    /// role at classification time. Using that one sensor's own history would silently stop
    /// tracking the actual hottest CCD once a different one overtakes it. Instead, for that
    /// role only, compute max(that sensor's history, every <see cref="Roles.CpuTempCcd"/>
    /// sensor's history) per sample, tail-aligned (histories are "last N samples" ring
    /// buffers, so index 0 doesn't necessarily mean the same wall-clock sample across
    /// sensors with different lengths).</summary>
    private static float?[] GetTemperatureHistory(TelemetrySnapshot snapshot, ClassificationResult classification, SensorRole role)
    {
        if (role.Role != Roles.CpuTempControl || !classification.CpuControlIsCcdMax)
        {
            return GetHistory(snapshot, role.SensorId);
        }

        return GetControlTempMaxHistory(snapshot, classification, role.SensorId);
    }

    private static float?[] GetControlTempMaxHistory(TelemetrySnapshot snapshot, ClassificationResult classification, string controlSensorId)
    {
        var ccdSensorIds = classification.Roles
            .Where(r => r.Role == Roles.CpuTempCcd)
            .Select(r => r.SensorId)
            .Append(controlSensorId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var histories = ccdSensorIds.Select(id => GetHistory(snapshot, id)).Where(h => h.Length > 0).ToArray();
        if (histories.Length == 0)
        {
            return GetHistory(snapshot, controlSensorId);
        }

        var length = histories.Max(h => h.Length);
        var result = new float?[length];
        for (var i = 0; i < length; i++)
        {
            float? max = null;
            foreach (var history in histories)
            {
                var offset = length - history.Length;
                if (i < offset)
                {
                    continue;
                }

                if (history[i - offset] is { } value && float.IsFinite(value) && (max is null || value > max))
                {
                    max = value;
                }
            }

            result[i] = max;
        }

        return result;
    }

    private static string EvaluateGenericTemp(SensorRole role, float value, ThresholdEntry threshold, List<Concern> concerns)
    {
        var label = RoleLabels.For(role.Role);
        if (value >= threshold.Critical)
        {
            concerns.Add(new Concern(
                ConcernLevel.Critical, role.Role, role.SensorId,
                $"{label} at {Format(value)} {RoleLabels.DegreeCelsius}, over critical ({Format(threshold.Critical)} {RoleLabels.DegreeCelsius})"));
            return ConcernLevel.Critical;
        }

        if (value >= threshold.Watch)
        {
            concerns.Add(new Concern(
                ConcernLevel.Watch, role.Role, role.SensorId,
                $"{label} at {Format(value)} {RoleLabels.DegreeCelsius}, at watch ({Format(threshold.Watch)} {RoleLabels.DegreeCelsius})"));
            return ConcernLevel.Watch;
        }

        return LevelOk;
    }

    private static string EvaluateCpuControlTemp(
        SensorRole role,
        float value,
        ThresholdEntry threshold,
        TelemetrySnapshot snapshot,
        ClassificationResult classification,
        List<Concern> concerns)
    {
        var label = RoleLabels.For(role.Role);
        if (value < threshold.Watch)
        {
            return LevelOk;
        }

        if (value < threshold.Critical)
        {
            concerns.Add(new Concern(
                ConcernLevel.Watch, role.Role, role.SensorId,
                $"{label} at {Format(value)} {RoleLabels.DegreeCelsius}, at watch ({Format(threshold.Watch)} {RoleLabels.DegreeCelsius})"));
            return ConcernLevel.Watch;
        }

        var overLimit = value > threshold.Critical + 2;
        var clockCollapsed = IsClockCollapsed(snapshot, classification);
        if (overLimit || clockCollapsed)
        {
            concerns.Add(new Concern(
                ConcernLevel.Critical, role.Role, role.SensorId,
                $"{label} at {Format(value)} {RoleLabels.DegreeCelsius}, over critical ({Format(threshold.Critical)} {RoleLabels.DegreeCelsius})"));
            return ConcernLevel.Critical;
        }

        concerns.Add(new Concern(
            ConcernLevel.Watch, role.Role, role.SensorId,
            "at thermal limit, boost reduced - normal for this CPU under full load"));
        return ConcernLevel.Watch;
    }

    private static bool IsClockCollapsed(TelemetrySnapshot snapshot, ClassificationResult classification)
    {
        var (enough, current, sessionMax) = UnderLoad(snapshot, classification, Roles.CpuLoadTotal, 90, Roles.CpuClockEffectiveAvg);
        return enough && current is { } cur && sessionMax is { } max && max > 0 && cur < max * 0.60;
    }

    // ---- Rule 3: trends --------------------------------------------------------------

    private static void EvaluateTrends(
        TelemetrySnapshot snapshot,
        ClassificationResult classification,
        ThresholdResolution thresholds,
        List<Concern> concerns,
        List<Trend> trends)
    {
        foreach (var role in classification.Roles)
        {
            if (!thresholds.Thresholds.TryGetValue(role.SensorId, out var threshold))
            {
                continue;
            }

            var history = GetTemperatureHistory(snapshot, classification, role);
            var window = history.Length > TrendWindow ? history[^TrendWindow..] : history;

            var points = new List<(double X, double Y)>();
            for (var i = 0; i < window.Length; i++)
            {
                if (window[i] is { } v && float.IsFinite(v))
                {
                    points.Add((i, v));
                }
            }

            if (points.Count < MinTrendSamples)
            {
                continue;
            }

            var slopePerMin = LeastSquaresSlope(points) * SamplesPerMinute;
            if (Math.Abs(slopePerMin) <= TrendSlopeThreshold)
            {
                continue;
            }

            var currentValue = LatestValue(history);
            double? etaMinutes = null;
            if (currentValue is { } cv && float.IsFinite(cv) && slopePerMin > 0)
            {
                var eta = (threshold.Critical - cv) / slopePerMin;
                etaMinutes = eta >= 0 ? eta : null;
            }

            trends.Add(new Trend(role.SensorId, role.Role, slopePerMin, etaMinutes, RoleLabels.For(role.Role)));

            if (currentValue is { } value &&
                etaMinutes is { } gatedEta &&
                gatedEta < TrendEtaMinutesGate &&
                value >= threshold.Watch - TrendValueGateOffset)
            {
                concerns.Add(new Concern(
                    ConcernLevel.Watch, role.Role, role.SensorId,
                    $"{RoleLabels.For(role.Role)} on track to hit {Format(threshold.Critical)} {RoleLabels.DegreeCelsius} in ~{Format(gatedEta)} min"));
            }
        }
    }

    private static double LeastSquaresSlope(List<(double X, double Y)> points)
    {
        var n = points.Count;
        var sumX = points.Sum(p => p.X);
        var sumY = points.Sum(p => p.Y);
        var sumXY = points.Sum(p => p.X * p.Y);
        var sumXX = points.Sum(p => p.X * p.X);
        var denominator = (n * sumXX) - (sumX * sumX);
        return denominator == 0 ? 0 : ((n * sumXY) - (sumX * sumY)) / denominator;
    }

    // ---- Rule 4: throttle detection ---------------------------------------------------

    private static void EvaluateThrottle(TelemetrySnapshot snapshot, ClassificationResult classification, List<Concern> concerns)
    {
        var (cpuEnough, cpuCurrent, cpuMax) = UnderLoad(snapshot, classification, Roles.CpuLoadTotal, 90, Roles.CpuClockEffectiveAvg);
        if (cpuEnough && cpuCurrent is { } cc && cpuMax is { } cm && cm > 0 && cc < cm * 0.85 &&
            TryGetSingleSensorId(classification, Roles.CpuClockEffectiveAvg, out var cpuSensorId))
        {
            concerns.Add(new Concern(ConcernLevel.Watch, Roles.CpuClockEffectiveAvg, cpuSensorId, "boost reduced"));
        }

        var (gpuEnough, gpuCurrent, gpuMax) = UnderLoad(snapshot, classification, Roles.GpuLoadCore, 95, Roles.GpuClockCore);
        if (gpuEnough && gpuCurrent is { } gc && gpuMax is { } gm && gm > 0 && gc < gm * 0.70 &&
            TryGetSingleSensorId(classification, Roles.GpuClockCore, out var gpuSensorId))
        {
            concerns.Add(new Concern(ConcernLevel.Watch, Roles.GpuClockCore, gpuSensorId, "boost reduced"));
        }
    }

    /// <summary>The "session max-under-load" for <paramref name="metricRole"/>: the highest
    /// value observed anywhere in the (up to 300-sample / 5-minute) history window at an
    /// index where <paramref name="loadRole"/> exceeded <paramref name="loadGatePercent"/>.
    /// A load collapse older than the window falls out of it (documented limitation: "session"
    /// here means "within the ring buffer", not "since sampler start").</summary>
    private static (bool HasEnoughSamples, float? Current, float? SessionMax) UnderLoad(
        TelemetrySnapshot snapshot,
        ClassificationResult classification,
        string loadRole,
        double loadGatePercent,
        string metricRole)
    {
        if (!TryGetSingleSensorId(classification, loadRole, out var loadId) ||
            !TryGetSingleSensorId(classification, metricRole, out var metricId))
        {
            return (false, null, null);
        }

        var loadHistory = GetHistory(snapshot, loadId);
        var metricHistory = GetHistory(snapshot, metricId);
        var length = Math.Min(loadHistory.Length, metricHistory.Length);

        float? sessionMax = null;
        var underLoadSamples = 0;
        for (var i = 0; i < length; i++)
        {
            if (loadHistory[i] is not { } load || !float.IsFinite(load) || load <= loadGatePercent)
            {
                continue;
            }

            underLoadSamples++;
            if (metricHistory[i] is { } metric && float.IsFinite(metric) && (sessionMax is null || metric > sessionMax))
            {
                sessionMax = metric;
            }
        }

        var currentLoad = length > 0 ? loadHistory[length - 1] : null;
        var currentMetric = length > 0 ? metricHistory[length - 1] : null;
        var isCurrentlyUnderLoad = currentLoad is { } cl && float.IsFinite(cl) && cl > loadGatePercent;

        return (underLoadSamples >= UnderLoadMinSamples && isCurrentlyUnderLoad, currentMetric, sessionMax);
    }

    // ---- Rule 5: power limit (informational) ------------------------------------------

    private static void EvaluatePowerLimit(TelemetrySnapshot snapshot, ClassificationResult classification, List<Concern> concerns)
    {
        if (!TryGetSingleSensorId(classification, Roles.GpuPowerPct, out var sensorId))
        {
            return;
        }

        var history = GetHistory(snapshot, sensorId);
        if (history.Length < PowerLimitedSamples)
        {
            return;
        }

        var tail = history[^PowerLimitedSamples..];
        if (tail.All(v => v is { } f && float.IsFinite(f) && f >= 98))
        {
            concerns.Add(new Concern(ConcernLevel.Info, Roles.GpuPowerPct, sensorId, "power-limited"));
        }
    }

    // ---- Rule 6: fan/pump stall ---------------------------------------------------------

    private static void EvaluateFanStall(TelemetrySnapshot snapshot, ClassificationResult classification, List<Concern> concerns)
    {
        float? gpuCoreTemp = TryGetSingleSensorId(classification, Roles.GpuTempCore, out var gpuTempId)
            ? LatestValue(GetHistory(snapshot, gpuTempId))
            : null;

        foreach (var role in classification.Roles)
        {
            if (!FanStallRoles.Contains(role.Role))
            {
                continue;
            }

            var history = GetHistory(snapshot, role.SensorId);
            if (history.Length == 0)
            {
                continue;
            }

            var connectedWindow = history.Length > FanConnectedWindow ? history[^FanConnectedWindow..] : history;
            var everConnected = connectedWindow.Any(v => v is { } f && float.IsFinite(f) && f > 300);
            if (!everConnected)
            {
                continue;
            }

            var tailLength = Math.Min(FanStallSamples, history.Length);
            var tail = history[^tailLength..];
            var stalled = tailLength >= FanStallSamples && tail.All(v => v is null || v is 0f);
            if (!stalled)
            {
                continue;
            }

            if (role.Role == Roles.GpuFan && gpuCoreTemp is { } gt && gt < 60)
            {
                continue;
            }

            var level = CriticalFanRoles.Contains(role.Role) ? ConcernLevel.Critical : ConcernLevel.Watch;
            concerns.Add(new Concern(level, role.Role, role.SensorId, $"{RoleLabels.For(role.Role)} stopped"));
        }
    }

    // ---- Rule 7: load phase --------------------------------------------------------------

    private static string ComputeLoadPhase(TelemetrySnapshot snapshot, ClassificationResult classification)
    {
        var cpu = Average30s(snapshot, classification, Roles.CpuLoadTotal) ?? 0f;
        var gpu = Average30s(snapshot, classification, Roles.GpuLoadCore) ?? 0f;

        if (cpu < 15 && gpu < 15)
        {
            return LoadPhase.Idle;
        }

        if (cpu > 80 && gpu < 50)
        {
            return LoadPhase.CpuBound;
        }

        if (gpu > 80 && cpu < 60)
        {
            return LoadPhase.GpuBound;
        }

        return LoadPhase.Combined;
    }

    private static float? Average30s(TelemetrySnapshot snapshot, ClassificationResult classification, string role)
    {
        if (!TryGetSingleSensorId(classification, role, out var sensorId))
        {
            return null;
        }

        var history = GetHistory(snapshot, sensorId);
        if (history.Length == 0)
        {
            return null;
        }

        var window = history.Length > 30 ? history[^30..] : history;
        var valid = window.Where(v => v is { } f && float.IsFinite(f)).Select(v => v!.Value).ToArray();
        return valid.Length == 0 ? null : valid.Average();
    }

    // ---- Rule 8: headline ------------------------------------------------------------

    private static string BuildHeadline(HealthStatus status, string phase, TelemetrySnapshot snapshot, ClassificationResult classification)
    {
        var cpuTemp = SingleValue(snapshot, classification, Roles.CpuTempControl);
        var gpuTemp = SingleValue(snapshot, classification, Roles.GpuTempCore);
        var gpuPower = SingleValue(snapshot, classification, Roles.GpuPower);

        var parts = new List<string> { HeadlineCase(status), phase };
        if (cpuTemp is { } ct)
        {
            parts.Add($"CPU {Format(ct)} {RoleLabels.DegreeCelsius}");
        }

        if (gpuTemp is { } gt)
        {
            parts.Add(gpuPower is { } gp
                ? $"GPU {Format(gt)} {RoleLabels.DegreeCelsius} @ {Format(gp)} W"
                : $"GPU {Format(gt)} {RoleLabels.DegreeCelsius}");
        }

        return string.Join(", ", parts) + ".";
    }

    private static float? SingleValue(TelemetrySnapshot snapshot, ClassificationResult classification, string role)
    {
        var match = classification.Roles.FirstOrDefault(r => r.Role == role);
        return match is null ? null : LatestValue(GetTemperatureHistory(snapshot, classification, match));
    }

    // ---- Shared helpers ----------------------------------------------------------------

    private static bool TryGetSingleSensorId(ClassificationResult classification, string role, out string sensorId)
    {
        var match = classification.Roles.FirstOrDefault(r => r.Role == role);
        sensorId = match?.SensorId ?? string.Empty;
        return match is not null;
    }

    private static float?[] GetHistory(TelemetrySnapshot snapshot, string sensorId) =>
        snapshot.History.TryGetValue(sensorId, out var history) ? (history as float?[] ?? history.ToArray()) : [];

    private static float? LatestValue(float?[] history) => history.Length > 0 ? history[^1] : null;

    private static string Format(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Readable Title Case for the headline sentence ("Healthy, combined, ..."); the
    /// wire-format <c>status</c> field and the UI's status badge stay upper case (docs/SPEC.md
    /// step5-polish "Tiles consistency": "keep the status badge uppercase").</summary>
    private static string HeadlineCase(HealthStatus status) => status switch
    {
        HealthStatus.HEALTHY => "Healthy",
        HealthStatus.WATCH => "Watch",
        HealthStatus.CRITICAL => "Critical",
        _ => "Unknown",
    };
}
