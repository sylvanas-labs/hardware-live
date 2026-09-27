using HardwareLive.Core.Classification;
using HardwareLive.Protocol;

namespace HardwareLive.Tests.Classification;

public sealed class ClassifierUnitTests
{
    [Fact]
    public void DistanceToTjMaxNeverGetsARole()
    {
        var frame = ClassificationFixtures.LoadFrame("synthetic-intel-13700k_amd-7900xtx_desktop");

        var result = SensorClassifier.Classify(frame);

        var distanceSensorIds = frame.Sensors
            .Where(s => s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id)
            .ToList();
        Assert.NotEmpty(distanceSensorIds);
        Assert.All(distanceSensorIds, id => Assert.DoesNotContain(result.Roles, r => r.SensorId == id));
        Assert.All(distanceSensorIds, id => Assert.Contains(result.Limits, l => l.SensorId == id && l.Kind == LimitKinds.TjmaxDistance));
    }

    [Fact]
    public void DimmAndNvmeLimitSensorsNeverGetTempRoles()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");

        var result = SensorClassifier.Classify(frame);

        Assert.NotEmpty(result.Limits);
        foreach (var limit in result.Limits)
        {
            Assert.DoesNotContain(result.Roles, r => r.SensorId == limit.SensorId);
        }
    }

    [Fact]
    public void SuperIoCpuTempIsBoardTempCpuNotControlTemp()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var cpuTempSensor = frame.Sensors.Single(s => s.HardwareId == "/lpc/it8696e/0" && s.Name == "CPU" && s.Type == "Temperature");

        var result = SensorClassifier.Classify(frame);

        var role = Assert.Single(result.Roles, r => r.SensorId == cpuTempSensor.Id);
        Assert.Equal(Roles.BoardTempCpu, role.Role);
        Assert.DoesNotContain(result.Roles, r => r.Role == Roles.CpuTempControl && r.SensorId == cpuTempSensor.Id);
    }

    [Fact]
    public void JunkAndHtmlNamesDoNotThrow()
    {
        foreach (var fixtureName in ClassificationFixtures.AllFixtureNames)
        {
            var frame = ClassificationFixtures.LoadFrame(fixtureName);
            var exception = Record.Exception(() => SensorClassifier.Classify(frame));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void EmptyFrameYieldsOnlyMissingCpuTempControlAndDoesNotThrow()
    {
        var frame = new SensorFrame(SensorFrame.CurrentVersion, 1, 1000, true, true, "0.9.6.0", [], []);

        var result = SensorClassifier.Classify(frame);

        Assert.Empty(result.Roles);
        Assert.Empty(result.Limits);
        Assert.Null(result.PrimaryCpuId);
        Assert.Null(result.PrimaryGpuId);
        Assert.Equal([Roles.CpuTempControl], result.MissingMandatory);
    }

    [Fact]
    public void NullFrameYieldsEmptyResultAndDoesNotThrow()
    {
        var exception = Record.Exception(() => SensorClassifier.Classify(null));

        Assert.Null(exception);
        Assert.Equal(ClassificationResult.Empty, SensorClassifier.Classify(null));
    }

    [Fact]
    public void SensorWithUnknownHardwareIdIsIgnoredSafely()
    {
        var hardware = new HardwareInfo[] { new("/amdcpu/0", "AMD CPU", "Cpu", null) };
        var sensors = new SensorReading[]
        {
            new("/amdcpu/0/temperature/0", "/amdcpu/0", "Core (Tctl/Tdie)", "Temperature", 50, 50, 50),
            new("/orphan/0/temperature/0", "/orphan/0", "Orphan Temp", "Temperature", 99, 99, 99),
        };
        var frame = new SensorFrame(SensorFrame.CurrentVersion, 1, 1000, true, true, "0.9.6.0", hardware, sensors);

        var exception = Record.Exception(() => SensorClassifier.Classify(frame));
        Assert.Null(exception);

        var result = SensorClassifier.Classify(frame);
        Assert.DoesNotContain(result.Roles, r => r.SensorId == "/orphan/0/temperature/0");
        Assert.Contains(result.Roles, r => r.SensorId == "/amdcpu/0/temperature/0" && r.Role == Roles.CpuTempControl);
    }
}
