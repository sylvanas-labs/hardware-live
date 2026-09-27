namespace HardwareLive.Core.Profiles;

/// <summary>Exact <c>origin</c> strings for a resolved <see cref="ThresholdEntry"/>, per
/// docs/SPEC.md's threshold-resolution order (first match wins).</summary>
public static class ThresholdOrigin
{
    public const string Override = "override";
    public const string Device = "device";
    public const string Profile = "profile";
    public const string Generic = "generic";
}

/// <summary>Exact <c>confidence</c> strings, per docs/SPEC.md's profile table.</summary>
public static class ThresholdConfidence
{
    public const string Vendor = "vendor";
    public const string Secondary = "secondary";
    public const string Community = "community";
}

/// <summary>
/// A resolved watch/critical pair for one sensor id. <see cref="Source"/> and
/// <see cref="Confidence"/> are plain strings (not enums) so the JSON payload matches
/// docs/SPEC.md's exact lowercase vocabulary regardless of the router's enum-naming
/// policy (see RequestRouter's un-policied <c>JsonStringEnumConverter</c>).
/// </summary>
public sealed record ThresholdEntry(double Watch, double Critical, string Source, string Confidence, string Origin);

/// <summary>The full threshold-resolution result for a frame: one entry per sensor id that
/// carries a temperature role, plus the matched profile display names (component 4).</summary>
public sealed record ThresholdResolution(
    IReadOnlyDictionary<string, ThresholdEntry> Thresholds,
    string? CpuProfile,
    string? GpuProfile)
{
    public static readonly ThresholdResolution Empty = new(new Dictionary<string, ThresholdEntry>(StringComparer.Ordinal), null, null);
}

/// <summary>One role's limit inside a profile entry or the generic fallback table. Either
/// <see cref="Limit"/> (the CPU/GPU "watch = limit-7, critical = limit" shorthand) or the
/// explicit <see cref="Watch"/>/<see cref="Critical"/> pair is present, never both.</summary>
public sealed record ProfileLimit(
    string Role,
    double? Limit,
    double? Watch,
    double? Critical,
    string? Confidence,
    string? Source)
{
    public (double Watch, double Critical) Resolve() =>
        Limit is { } limit ? (limit - 7, limit) : (Watch!.Value, Critical!.Value);
}

/// <summary>One name-matched profile entry (a CPU or GPU model family) from profiles.json.
/// <see cref="Confidence"/>/<see cref="Source"/> are entry-level defaults that a
/// <see cref="ProfileLimit"/> may override.</summary>
public sealed record ProfileEntry(
    string Name,
    string Pattern,
    string? Confidence,
    string? Source,
    IReadOnlyList<ProfileLimit> Limits);

public sealed record ProfileTable(
    IReadOnlyList<ProfileEntry> Cpu,
    IReadOnlyList<ProfileEntry> Gpu,
    IReadOnlyList<ProfileLimit> Generic);
