using System.Buffers.Binary;
using System.Text.Json;

namespace HardwareLive.Protocol;

public static class FrameProtocol
{
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static async ValueTask WriteAsync(
        Stream stream,
        SensorFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);

        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SensorFrameJson.Options);
        if (payload.Length == 0)
        {
            throw new InvalidFrameException("A frame payload cannot be empty.");
        }

        if (payload.Length > MaxFrameBytes)
        {
            throw new FrameTooLargeException((uint)payload.Length);
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<SensorFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[sizeof(uint)];
        var prefixBytes = await ReadUpToAsync(stream, prefix, cancellationToken);
        if (prefixBytes == 0)
        {
            return null;
        }

        if (prefixBytes != prefix.Length)
        {
            throw new InvalidFrameException("The stream ended in the middle of a frame length prefix.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0)
        {
            throw new InvalidFrameException("A frame payload cannot be empty.");
        }

        if (length > MaxFrameBytes)
        {
            throw new FrameTooLargeException(length);
        }

        var payload = new byte[(int)length];
        var payloadBytes = await ReadUpToAsync(stream, payload, cancellationToken);
        if (payloadBytes != payload.Length)
        {
            throw new InvalidFrameException("The stream ended in the middle of a frame payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<SensorFrame>(payload, SensorFrameJson.Options)
                ?? throw new InvalidFrameException("The frame payload contained JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidFrameException("The frame payload was not a valid sensor frame.", exception);
        }
    }

    private static async ValueTask<int> ReadUpToAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}

public class InvalidFrameException : IOException
{
    public InvalidFrameException(string message)
        : base(message)
    {
    }

    public InvalidFrameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FrameTooLargeException : InvalidFrameException
{
    public FrameTooLargeException(uint frameLength)
        : base($"Frame length {frameLength} exceeds the {FrameProtocol.MaxFrameBytes}-byte limit.")
    {
        FrameLength = frameLength;
    }

    public uint FrameLength { get; }
}
