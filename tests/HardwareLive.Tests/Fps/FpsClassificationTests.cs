using HardwareLive.Core.Classification;
using HardwareLive.Core.Fps;
using HardwareLive.Protocol;

namespace HardwareLive.Tests.Fps;

/// <summary>docs/SPEC.md step7-fps item 5: the classifier must map the synthetic
/// <c>/fps/*</c> sensors <see cref="FpsService.Augment"/> appends. Uses hand-built frames
/// (not the golden fixtures -- none of those include an <c>/fps</c> hardware entry, so they
/// are intentionally untouched by this change).</summary>
public sealed class FpsClassificationTests
{
    [Fact]
    public void SyntheticFpsSensorsAreClassifiedByRole()
    {
        var hardware = new HardwareInfo(FpsSensorIds.HardwareId, FpsSensorIds.HardwareName, FpsSensorIds.HardwareType, null);
        var sensors = new SensorReading[]
        {
            new(FpsSensorIds.Avg, FpsSensorIds.HardwareId, "Average FPS", "Fps", 144f, null, null),
            new(FpsSensorIds.Low1, FpsSensorIds.HardwareId, "1% low FPS", "Fps", 120f, null, null),
            new(FpsSensorIds.FrametimeMs, FpsSensorIds.HardwareId, "Frame time", "FrameTime", 6.9f, null, null),
            new(FpsSensorIds.FrametimeJitter, FpsSensorIds.HardwareId, "Frame time jitter", "FrameTime", 0.5f, null, null),
        };
        var frame = new SensorFrame(1, 1, 0, true, false, "1.0", [hardware], sensors);

        var result = SensorClassifier.Classify(frame);

        Assert.Contains(result.Roles, r => r.SensorId == FpsSensorIds.Avg && r.Role == Roles.FpsAvg);
        Assert.Contains(result.Roles, r => r.SensorId == FpsSensorIds.Low1 && r.Role == Roles.FpsLow1);
        Assert.Contains(result.Roles, r => r.SensorId == FpsSensorIds.FrametimeMs && r.Role == Roles.FrametimeMs);
        Assert.Contains(result.Roles, r => r.SensorId == FpsSensorIds.FrametimeJitter && r.Role == Roles.FrametimeJitter);
    }

    [Fact]
    public void AugmentAppendsSyntheticSensorsWithNullValuesWhenNoTarget()
    {
        var service = new FpsService(
            Path.Combine(Path.GetTempPath(), $"hl-fps-{Guid.NewGuid():N}", "config.json"),
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.exe"));

        var baseFrame = new SensorFrame(1, 1, 0, true, false, "1.0", [], []);
        var augmented = service.Augment(baseFrame);

        Assert.Contains(augmented.Hardware, h => h.Id == FpsSensorIds.HardwareId);
        var avgSensor = Assert.Single(augmented.Sensors, s => s.Id == FpsSensorIds.Avg);
        Assert.Null(avgSensor.Value);

        Assert.Equal(FpsStatus.Disabled, service.Current.Status);
    }
}
