using HardwareLive.Protocol;

namespace HardwareLive.Tests;

public sealed class UniqueIdsTests
{
    [Fact]
    public void FirstOccurrenceKeepsItsId()
    {
        var ids = new UniqueIds();

        Assert.Equal("/gpu-nvidia/0/load/3", ids.MakeUnique("/gpu-nvidia/0/load/3"));
        Assert.Equal("/gpu-nvidia/0/load/4", ids.MakeUnique("/gpu-nvidia/0/load/4"));
    }

    [Fact]
    public void RepeatedIdsGetStableSuffixes()
    {
        // Real LHM 0.9.6 on an RTX 5090: "GPU Bus" and "GPU Memory" load share load/3.
        var ids = new UniqueIds();

        Assert.Equal("/gpu-nvidia/0/load/3", ids.MakeUnique("/gpu-nvidia/0/load/3"));
        Assert.Equal("/gpu-nvidia/0/load/3~2", ids.MakeUnique("/gpu-nvidia/0/load/3"));
        Assert.Equal("/gpu-nvidia/0/load/3~3", ids.MakeUnique("/gpu-nvidia/0/load/3"));
    }

    [Fact]
    public void SuffixNeverCollidesWithAGenuineId()
    {
        var ids = new UniqueIds();

        Assert.Equal("/x", ids.MakeUnique("/x"));
        Assert.Equal("/x~2", ids.MakeUnique("/x~2"));
        Assert.Equal("/x~3", ids.MakeUnique("/x"));
    }

    [Fact]
    public void SameSequenceProducesSameIdsAcrossFrames()
    {
        string[] frameOrder = ["/a", "/b", "/a", "/c", "/a"];

        var first = new UniqueIds();
        var second = new UniqueIds();

        Assert.Equal(frameOrder.Select(first.MakeUnique), frameOrder.Select(second.MakeUnique));
    }

    [Fact]
    public void ValidatorRejectsDuplicateSensorIds()
    {
        var frame = ValidFrame(
            sensors:
            [
                new SensorReading("/hw/0/load/3", "/hw/0", "GPU Bus", "Load", 1f, null, null),
                new SensorReading("/hw/0/load/3", "/hw/0", "GPU Memory", "Load", 2f, null, null),
            ]);

        Assert.False(FrameValidator.Validate(frame));
    }

    [Fact]
    public void ValidatorRejectsDuplicateHardwareIds()
    {
        var frame = ValidFrame(
            hardware:
            [
                new HardwareInfo("/hw/0", "One", "GpuNvidia", null),
                new HardwareInfo("/hw/0", "Two", "GpuNvidia", null),
            ]);

        Assert.False(FrameValidator.Validate(frame));
    }

    [Fact]
    public void ValidatorAcceptsDeduplicatedFrame()
    {
        var frame = ValidFrame(
            sensors:
            [
                new SensorReading("/hw/0/load/3", "/hw/0", "GPU Bus", "Load", 1f, null, null),
                new SensorReading("/hw/0/load/3~2", "/hw/0", "GPU Memory", "Load", 2f, null, null),
            ]);

        Assert.True(FrameValidator.Validate(frame));
    }

    private static SensorFrame ValidFrame(
        IReadOnlyList<HardwareInfo>? hardware = null,
        IReadOnlyList<SensorReading>? sensors = null) =>
        new(
            SensorFrame.CurrentVersion,
            1,
            0,
            true,
            true,
            "0.9.6.0",
            hardware ?? [new HardwareInfo("/hw/0", "GPU", "GpuNvidia", null)],
            sensors ?? [new SensorReading("/hw/0/load/0", "/hw/0", "GPU Core", "Load", 1f, null, null)]);
}
