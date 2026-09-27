using System.Text.Json;

namespace HardwareLive.App;

/// <summary>
/// Reads the top-level <c>openWindowOnStart</c> boolean from <c>config.json</c> (docs/SPEC.md
/// Component 8 step 5). Follows the same "never throws, default wins" contract as
/// <see cref="HardwareLive.Core.Profiles.UserThresholdConfig"/>: a missing file, malformed
/// JSON, or a non-boolean value all fall back to the default (true) rather than blocking
/// startup or crashing.
/// </summary>
public static class AppStartupConfig
{
    public static bool ReadOpenWindowOnStart(string configPath)
    {
        const bool defaultValue = true;

        if (!File.Exists(configPath))
        {
            return defaultValue;
        }

        try
        {
            if (new FileInfo(configPath).Length > 64 * 1024)
            {
                return defaultValue;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("openWindowOnStart", out var element))
            {
                return defaultValue;
            }

            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue,
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return defaultValue;
        }
    }
}
