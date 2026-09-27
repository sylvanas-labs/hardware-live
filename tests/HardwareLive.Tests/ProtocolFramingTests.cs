using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HardwareLive.Protocol;

namespace HardwareLive.Tests;

public sealed class ProtocolFramingTests
{
    [Fact]
    public async Task FrameRoundTrips()
    {
        var expected = CreateFrame();
        await using var stream = new MemoryStream();

        await FrameProtocol.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await FrameProtocol.ReadAsync(stream);

        AssertFramesEqual(expected, actual);
    }

    [Fact]
    public async Task ReaderHandlesOneByteReads()
    {
        await using var serialized = new MemoryStream();
        await FrameProtocol.WriteAsync(serialized, CreateFrame());
        await using var stream = new OneByteReadStream(serialized.ToArray());

        var actual = await FrameProtocol.ReadAsync(stream);

        AssertFramesEqual(CreateFrame(), actual);
    }

    [Fact]
    public async Task ZeroLengthIsInvalid()
    {
        await using var stream = new MemoryStream(new byte[sizeof(uint)]);

        await Assert.ThrowsAsync<InvalidFrameException>(
            async () => await FrameProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task OversizeLengthIsRejectedBeforeReadingOrAllocatingPayload()
    {
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, FrameProtocol.MaxFrameBytes + 1u);
        await using var stream = new MemoryStream(prefix);

        var exception = await Assert.ThrowsAsync<FrameTooLargeException>(
            async () => await FrameProtocol.ReadAsync(stream));

        Assert.Equal(FrameProtocol.MaxFrameBytes + 1u, exception.FrameLength);
        Assert.Equal(prefix.Length, stream.Position);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task EofMidFrameIsInvalid(int bytesToKeep)
    {
        await using var complete = new MemoryStream();
        await FrameProtocol.WriteAsync(complete, CreateFrame());
        await using var truncated = new MemoryStream(complete.ToArray()[..bytesToKeep]);

        await Assert.ThrowsAsync<InvalidFrameException>(
            async () => await FrameProtocol.ReadAsync(truncated));
    }

    [Fact]
    public async Task CleanEofBetweenFramesReturnsNull()
    {
        await using var stream = new MemoryStream();

        Assert.Null(await FrameProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task NonFiniteValuesSerializeAsNull()
    {
        var frame = CreateFrame() with
        {
            Sensors =
            [
                new SensorReading("/cpu/0/temp/0", "/cpu/0", "Package", "Temperature", float.NaN, float.NegativeInfinity, float.PositiveInfinity),
            ],
        };
        await using var stream = new MemoryStream();

        await FrameProtocol.WriteAsync(stream, frame);
        var json = Encoding.UTF8.GetString(stream.ToArray()[sizeof(uint)..]);
        stream.Position = 0;
        var roundTripped = await FrameProtocol.ReadAsync(stream);

        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        var sensor = Assert.Single(roundTripped!.Sensors);
        Assert.Null(sensor.Value);
        Assert.Null(sensor.Min);
        Assert.Null(sensor.Max);
    }

    [Fact]
    public void IndentedJsonUsesCamelCase()
    {
        var json = JsonSerializer.Serialize(CreateFrame(), SensorFrameJson.CreateIndentedOptions());

        Assert.Contains("\n", json, StringComparison.Ordinal);
        Assert.Contains("\"timestampUnixMs\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TimestampUnixMs\"", json, StringComparison.Ordinal);
    }

    internal static SensorFrame CreateFrame(long sequence = 7, long timestampUnixMs = 1234) =>
        new(
            SensorFrame.CurrentVersion,
            sequence,
            timestampUnixMs,
            PawnIoInstalled: true,
            Elevated: false,
            LhmVersion: "0.9.6.0",
            Hardware: [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            Sensors: [new SensorReading("/cpu/0/temp/0", "/cpu/0", "Package", "Temperature", 42.5f, 40f, 50f)]);

    private static void AssertFramesEqual(SensorFrame expected, SensorFrame? actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected, SensorFrameJson.Options),
            JsonSerializer.Serialize(actual, SensorFrameJson.Options));

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
