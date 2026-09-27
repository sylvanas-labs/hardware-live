using System.Text.Json;

namespace HardwareLive.Core.Profiles;

/// <summary>
/// Loads and caches the embedded <c>profiles.json</c> vendor threshold table (docs/SPEC.md
/// Component 4). Loaded once per process: the table is static data shipped with the build,
/// never edited at runtime.
/// </summary>
public static class ThresholdProfiles
{
    private const string ResourceName = "HardwareLive.Core.Profiles.profiles.json";

    private static readonly Lazy<ProfileTable> LazyTable = new(Load);

    public static ProfileTable Table => LazyTable.Value;

    private static ProfileTable Load()
    {
        using var stream = typeof(ThresholdProfiles).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");

        var document = JsonSerializer.Deserialize<ProfilesJson>(stream, JsonOptions)
            ?? throw new InvalidOperationException("profiles.json deserialized to null.");

        return new ProfileTable(
            (document.Cpu ?? []).Select(ToEntry).ToList(),
            (document.Gpu ?? []).Select(ToEntry).ToList(),
            (document.Generic ?? []).Select(ToLimit).ToList());
    }

    private static ProfileEntry ToEntry(ProfileEntryJson json) =>
        new(
            json.Name ?? throw new InvalidOperationException("profiles.json entry missing 'name'."),
            json.Pattern ?? throw new InvalidOperationException("profiles.json entry missing 'pattern'."),
            json.Confidence,
            json.Source,
            (json.Limits ?? []).Select(ToLimit).ToList());

    private static ProfileLimit ToLimit(ProfileLimitJson json) =>
        new(
            json.Role ?? throw new InvalidOperationException("profiles.json limit missing 'role'."),
            json.Limit,
            json.Watch,
            json.Critical,
            json.Confidence,
            json.Source);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ProfilesJson
    {
        public List<ProfileEntryJson>? Cpu { get; set; }
        public List<ProfileEntryJson>? Gpu { get; set; }
        public List<ProfileLimitJson>? Generic { get; set; }
    }

    private sealed class ProfileEntryJson
    {
        public string? Name { get; set; }
        public string? Pattern { get; set; }
        public string? Confidence { get; set; }
        public string? Source { get; set; }
        public List<ProfileLimitJson>? Limits { get; set; }
    }

    private sealed class ProfileLimitJson
    {
        public string? Role { get; set; }
        public double? Limit { get; set; }
        public double? Watch { get; set; }
        public double? Critical { get; set; }
        public string? Confidence { get; set; }
        public string? Source { get; set; }
    }
}
