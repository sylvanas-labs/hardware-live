using System.Text.Json;
using System.Text.Json.Nodes;

namespace HardwareLive.Core.Fps;

/// <summary>
/// The <c>fps</c> section of <c>config.json</c> (docs/SPEC.md step7-fps item 1):
/// <c>{ "fps": { "enabled": bool, "pinnedProcess"?: string, "denylistExtra"?: [string] } }</c>.
/// Parsing never throws -- mirrors <see cref="Profiles.UserThresholdConfig"/>'s lenient style:
/// a missing file or missing "fps" section is not an error (defaults, disabled); malformed
/// JSON or a wrong-shaped "fps" value is reported via <see cref="Invalid"/> but still yields
/// usable defaults, never a crash.
/// </summary>
public sealed record FpsUserConfig(bool Enabled, string? PinnedProcess, IReadOnlyList<string> DenylistExtra, bool Invalid)
{
    public static readonly FpsUserConfig Default = new(false, null, [], false);

    public static FpsUserConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Default with { Invalid = true };
            }

            if (!document.RootElement.TryGetProperty("fps", out var fpsElement))
            {
                return Default;
            }

            if (fpsElement.ValueKind != JsonValueKind.Object)
            {
                return Default with { Invalid = true };
            }

            var enabled = false;
            if (fpsElement.TryGetProperty("enabled", out var enabledElement))
            {
                if (enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    enabled = enabledElement.GetBoolean();
                }
                else
                {
                    return Default with { Invalid = true };
                }
            }

            string? pinned = null;
            if (fpsElement.TryGetProperty("pinnedProcess", out var pinnedElement) &&
                pinnedElement.ValueKind != JsonValueKind.Null)
            {
                if (pinnedElement.ValueKind != JsonValueKind.String)
                {
                    return Default with { Invalid = true };
                }

                pinned = pinnedElement.GetString();
                if (string.IsNullOrWhiteSpace(pinned))
                {
                    pinned = null;
                }
            }

            var denylistExtra = new List<string>();
            if (fpsElement.TryGetProperty("denylistExtra", out var denylistElement) &&
                denylistElement.ValueKind != JsonValueKind.Null)
            {
                if (denylistElement.ValueKind != JsonValueKind.Array)
                {
                    return Default with { Invalid = true };
                }

                foreach (var item in denylistElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return Default with { Invalid = true };
                    }

                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        denylistExtra.Add(value);
                    }
                }
            }

            return new FpsUserConfig(enabled, pinned, denylistExtra, Invalid: false);
        }
        catch (JsonException)
        {
            return Default with { Invalid = true };
        }
        catch (IOException)
        {
            return Default with { Invalid = true };
        }
    }

    /// <summary>
    /// Rewrites only the <c>fps</c> section of <paramref name="configPath"/>, preserving every
    /// other top-level key (e.g. <c>port</c>, <c>thresholds</c>) and any unrecognized key
    /// already under <c>fps</c>, via an atomic temp-file-then-replace write (mirrors
    /// <c>FileLayoutStore.Persist</c>). <paramref name="mutate"/> receives the current
    /// (possibly newly-created) <c>fps</c> JSON object to edit in place.
    /// </summary>
    public static void Update(string configPath, Action<JsonObject> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? ".");

        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                var parsed = JsonNode.Parse(File.ReadAllBytes(configPath));
                root = parsed as JsonObject ?? [];
            }
            catch (JsonException)
            {
                root = [];
            }
        }
        else
        {
            root = [];
        }

        var fps = root["fps"] as JsonObject ?? [];
        mutate(fps);
        root["fps"] = fps;

        var tempPath = configPath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            root.WriteTo(writer);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, configPath, overwrite: true);
    }
}
