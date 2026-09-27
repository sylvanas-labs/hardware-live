using System.Globalization;
using System.Text.Json;

namespace HardwareLive.Core;

public static class PortConfiguration
{
    public static int Resolve(IReadOnlyList<string> args, string configPath)
    {
        var port = ReadConfigPort(configPath) ?? HardwareLiveServer.DefaultPort;

        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--port", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Count ||
                !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out port))
            {
                throw new ArgumentException("--port requires an integer value.", nameof(args));
            }

            index++;
        }

        ValidatePort(port);
        return port;
    }

    private static int? ReadConfigPort(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("port", out var portElement))
            {
                return null;
            }

            if (!portElement.TryGetInt32(out var port))
            {
                throw new InvalidDataException("config.json port must be an integer.");
            }

            ValidatePort(port);
            return port;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("config.json must contain valid JSON.", exception);
        }
    }

    private static void ValidatePort(int port)
    {
        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");
        }
    }
}
