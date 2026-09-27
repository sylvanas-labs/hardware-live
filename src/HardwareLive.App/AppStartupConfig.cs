using System.Text.Json;
using System.Text.Json.Nodes;

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
    private const long MaxConfigBytes = 64 * 1024;

    /// <summary>
    /// Installer security fix (see install.ps1 Step 1): an elevated install process must never
    /// write <c>%LOCALAPPDATA%\HardwareLive\config.json</c> directly (a symlink/junction
    /// planted in that user-controlled folder could redirect the write anywhere the elevated
    /// token can reach). When install.ps1 could not make that write itself -- because it ran
    /// already elevated, with no unelevated phase -- it instead records the user's consent as
    /// <c>fpsConsent: true</c> in admin-owned <c>%ProgramData%\HardwareLive\install-state.json</c>
    /// and defers to this method, which runs unelevated (as the app always does) and so can
    /// safely write the per-user config itself.
    /// </summary>
    /// <param name="installStatePath">Admin-owned install-state.json; read-only here.</param>
    /// <param name="resolvedConfigPath">The config.json path <see cref="HardwareLive.Core.ConfigPaths.Resolve"/>
    /// currently resolves to (per-user if it exists, else the app-base-directory fallback).
    /// Read only, to check whether the user already made an explicit fps.enabled choice.</param>
    /// <param name="perUserConfigPath">The always-writable per-user config.json path. This is
    /// the only path ever written by this method.</param>
    /// <remarks>Never throws: a missing/malformed/oversized install-state.json or config.json
    /// is treated as "nothing to do" (or, for the config read, as an empty starting point) and
    /// the method returns without touching disk, matching every other config reader's "never
    /// block startup" contract.</remarks>
    public static void ApplyFpsConsentFromInstallState(string installStatePath, string resolvedConfigPath, string perUserConfigPath)
    {
        try
        {
            if (!TryReadFpsConsent(installStatePath, out var fpsConsent) || !fpsConsent)
            {
                return;
            }

            var existingConfig = TryReadJsonObject(resolvedConfigPath) ?? new JsonObject();

            if (existingConfig.TryGetPropertyValue("fps", out var fpsNode) &&
                fpsNode is JsonObject fpsObject &&
                fpsObject.ContainsKey("enabled"))
            {
                // The user (or a previous run of this same method) already made an explicit
                // choice, present or absent -- never overwrite it.
                return;
            }

            var mergedFps = fpsNode is JsonObject existingFpsObject ? existingFpsObject.DeepClone().AsObject() : new JsonObject();
            mergedFps["enabled"] = true;

            var mergedConfig = existingConfig.DeepClone().AsObject();
            mergedConfig["fps"] = mergedFps;

            var directory = Path.GetDirectoryName(perUserConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // File.WriteAllBytes, not WriteAllText: WriteAllText with Encoding.UTF8 prepends a
            // BOM that JsonDocument.Parse chokes on when this file is read back.
            File.WriteAllBytes(perUserConfigPath, JsonSerializer.SerializeToUtf8Bytes(mergedConfig));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Never block app startup over this.
        }
    }

    private static bool TryReadFpsConsent(string installStatePath, out bool fpsConsent)
    {
        fpsConsent = false;

        if (!File.Exists(installStatePath) || new FileInfo(installStatePath).Length > MaxConfigBytes)
        {
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(installStatePath));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("fpsConsent", out var element))
        {
            return false;
        }

        fpsConsent = element.ValueKind == JsonValueKind.True;
        return true;
    }

    private static JsonObject? TryReadJsonObject(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes)
        {
            return null;
        }

        var node = JsonNode.Parse(File.ReadAllBytes(path));
        return node as JsonObject;
    }

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
