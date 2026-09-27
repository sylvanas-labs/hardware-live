using System.Text.Json;

namespace HardwareLive.Core.Profiles;

/// <summary>A user-supplied override for one role or exact sensor id. Either field may be
/// absent; the missing half falls back to whatever the next threshold-resolution tier would
/// have supplied (docs/SPEC.md "Threshold resolution" step 1).</summary>
public sealed record ThresholdOverride(double? Watch, double? Critical);

/// <summary>
/// The <c>thresholds</c> section of <c>config.json</c> (docs/SPEC.md "Config" section).
/// Parsing never throws: a missing file, malformed JSON, or an out-of-range value all
/// resolve to <see cref="Empty"/> with <see cref="Invalid"/> set (except a simply-missing
/// file, which is not an error). The caller surfaces <see cref="Invalid"/> as a health INFO
/// concern -- "never crash" is the hard requirement, per the spec's config section.
/// </summary>
public sealed class UserThresholdConfig
{
    public static readonly UserThresholdConfig Empty =
        new(new Dictionary<string, ThresholdOverride>(StringComparer.Ordinal), invalid: false);

    private readonly IReadOnlyDictionary<string, ThresholdOverride> _overrides;

    private UserThresholdConfig(IReadOnlyDictionary<string, ThresholdOverride> overrides, bool invalid)
    {
        _overrides = overrides;
        Invalid = invalid;
    }

    /// <summary>True when the file existed but couldn't be used as-is (malformed JSON, a
    /// non-object "thresholds" value, or a value outside the valid range); defaults were
    /// used instead. False for "file doesn't exist" (not an error).</summary>
    public bool Invalid { get; }

    public bool TryGet(string key, out ThresholdOverride value) => _overrides.TryGetValue(key, out value!);

    public static UserThresholdConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new UserThresholdConfig(Empty._overrides, invalid: true);
            }

            if (!document.RootElement.TryGetProperty("thresholds", out var thresholdsElement))
            {
                return Empty;
            }

            if (thresholdsElement.ValueKind != JsonValueKind.Object)
            {
                return new UserThresholdConfig(Empty._overrides, invalid: true);
            }

            var overrides = new Dictionary<string, ThresholdOverride>(StringComparer.Ordinal);
            foreach (var property in thresholdsElement.EnumerateObject())
            {
                if (!TryParseOverride(property.Value, out var value))
                {
                    return new UserThresholdConfig(Empty._overrides, invalid: true);
                }

                overrides[property.Name] = value;
            }

            return new UserThresholdConfig(overrides, invalid: false);
        }
        catch (JsonException)
        {
            return new UserThresholdConfig(Empty._overrides, invalid: true);
        }
        catch (IOException)
        {
            return new UserThresholdConfig(Empty._overrides, invalid: true);
        }
    }

    private static bool TryParseOverride(JsonElement element, out ThresholdOverride value)
    {
        value = new ThresholdOverride(null, null);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double? watch = null;
        double? critical = null;

        if (element.TryGetProperty("watch", out var watchElement))
        {
            if (!TryReadFiniteInRange(watchElement, out var w))
            {
                return false;
            }

            watch = w;
        }

        if (element.TryGetProperty("critical", out var criticalElement))
        {
            if (!TryReadFiniteInRange(criticalElement, out var c))
            {
                return false;
            }

            critical = c;
        }

        if (watch is null && critical is null)
        {
            return false;
        }

        if (watch is { } wv && critical is { } cv && wv > cv)
        {
            return false;
        }

        value = new ThresholdOverride(watch, critical);
        return true;
    }

    private static bool TryReadFiniteInRange(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var candidate))
        {
            return false;
        }

        if (!double.IsFinite(candidate) || candidate <= 0 || candidate > 150)
        {
            return false;
        }

        value = candidate;
        return true;
    }
}
