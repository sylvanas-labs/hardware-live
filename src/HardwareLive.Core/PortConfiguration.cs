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

    // config.json lives in the user-editable %LOCALAPPDATA%, so a typo there must never stop
    // the app from starting: any problem falls back to the default port. (The thresholds
    // section reports an invalid file as a health INFO concern.) An invalid --port on the
    // command line still throws, because that is a developer's explicit input.
    private static int? ReadConfigPort(string configPath)
    {
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > 64 * 1024)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("port", out var portElement) ||
                portElement.ValueKind != JsonValueKind.Number || // TryGetInt32 throws on non-numbers
                !portElement.TryGetInt32(out var port) ||
                port is < 0 or > ushort.MaxValue)
            {
                return null;
            }

            return port;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
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
