using HardwareLive.Protocol;

namespace HardwareLive.Tests;

public sealed class FrameValidatorTests
{
    [Fact]
    public void ValidFrameIsAccepted()
    {
        Assert.True(FrameValidator.Validate(ValidFrame()));
    }

    [Fact]
    public void WrongVersionIsRejected()
    {
        Assert.False(FrameValidator.Validate(ValidFrame() with { Version = 2 }));
    }

    [Fact]
    public void NullHardwareOrSensorsListIsRejected()
    {
        Assert.False(FrameValidator.Validate(ValidFrame() with { Hardware = null! }));
        Assert.False(FrameValidator.Validate(ValidFrame() with { Sensors = null! }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyOrNullIdIsRejected(string? id)
    {
        var frame = ValidFrame();
        var badSensor = frame.Sensors[0] with { Id = id! };
        Assert.False(FrameValidator.Validate(frame with { Sensors = [badSensor] }));
    }

    [Fact]
    public void OverlongStringIsRejected()
    {
        var frame = ValidFrame();
        var tooLong = new string('x', 257);
        var badSensor = frame.Sensors[0] with { Name = tooLong };
        Assert.False(FrameValidator.Validate(frame with { Sensors = [badSensor] }));
    }

    [Fact]
    public void StringAtExactlyTheLimitIsAccepted()
    {
        var frame = ValidFrame();
        var atLimit = new string('x', 256);
        var sensor = frame.Sensors[0] with { Name = atLimit };
        Assert.True(FrameValidator.Validate(frame with { Sensors = [sensor] }));
    }

    [Fact]
    public void TooManyHardwareEntriesIsRejected()
    {
        var hardware = Enumerable.Range(0, FrameValidator.MaxHardwareCount + 1)
            .Select(index => new HardwareInfo($"/hw/{index}", "Name", "Type", null))
            .ToArray();
        Assert.False(FrameValidator.Validate(ValidFrame() with { Hardware = hardware }));
    }

    [Fact]
    public void ExactlyTheHardwareCapIsAccepted()
    {
        var hardware = Enumerable.Range(0, FrameValidator.MaxHardwareCount)
            .Select(index => new HardwareInfo($"/hw/{index}", "Name", "Type", null))
            .ToArray();
        Assert.True(FrameValidator.Validate(ValidFrame() with { Hardware = hardware, Sensors = [] }));
    }

    [Fact]
    public void TooManySensorsIsRejected()
    {
        var sensors = Enumerable.Range(0, FrameValidator.MaxSensorCount + 1)
            .Select(index => new SensorReading($"/s/{index}", "/cpu/0", "Name", "Type", null, null, null))
            .ToArray();
        Assert.False(FrameValidator.Validate(ValidFrame() with { Sensors = sensors }));
    }

    [Fact]
    public void NullFrameIsRejected()
    {
        Assert.False(FrameValidator.Validate(null));
    }

    [Fact]
    public void EmptyHardwareAndSensorsAreValid()
    {
        Assert.True(FrameValidator.Validate(ValidFrame() with { Hardware = [], Sensors = [] }));
    }

    private static SensorFrame ValidFrame() =>
        new(
            SensorFrame.CurrentVersion,
            1,
            1000,
            PawnIoInstalled: true,
            Elevated: true,
            LhmVersion: "0.9.6.0",
            Hardware: [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            Sensors: [new SensorReading("/cpu/0/temp/0", "/cpu/0", "Package", "Temperature", 42.5f, null, null)]);
}
