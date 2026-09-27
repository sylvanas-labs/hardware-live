namespace HardwareLive.Core.Analysis;

/// <summary>Member names are the exact wire-format strings docs/SPEC.md requires
/// ("HEALTHY, WATCH, CRITICAL or UNKNOWN"); RequestRouter's <c>JsonStringEnumConverter</c>
/// has no naming policy, so the enum name is what ships as-is.</summary>
public enum HealthStatus
{
    HEALTHY,
    WATCH,
    CRITICAL,
    UNKNOWN,
}

/// <summary>Exact concern-level strings for <c>/api/health</c>'s <c>concerns[].level</c>.</summary>
public static class ConcernLevel
{
    public const string Info = "info";
    public const string Watch = "watch";
    public const string Critical = "critical";
}

/// <summary>Exact load-phase strings for <c>/api/health</c>'s <c>phase</c>.</summary>
public static class LoadPhase
{
    public const string Idle = "idle";
    public const string CpuBound = "cpu-bound";
    public const string GpuBound = "gpu-bound";
    public const string Combined = "combined";
}

/// <summary>One health concern. <see cref="Message"/> is always plain text built from the
/// role's label and numbers -- never a raw hardware/sensor name (docs/SPEC.md: those can
/// contain control characters, e.g. a real DIMM name with embedded CR/NUL bytes).</summary>
public sealed record Concern(string Level, string Role, string SensorId, string Message);

/// <summary><see cref="Label"/> is the role's plain-English title (docs/SPEC.md step5-polish
/// "Ambiguous labels": trends must not fall back to a raw sensor name like "CPU"); it
/// defaults so every existing 4-argument call site keeps compiling.</summary>
public sealed record Trend(string SensorId, string Role, double SlopePerMin, double? EtaMinutes, string Label = "");

/// <summary><see cref="AnalysisResult.SensorLevels"/> keys are sensor ids, values are the exact
/// three-state strings ("ok"/"watch"/"critical") the UI's own <c>thresholdLevel()</c> uses -- one
/// entry for every sensor that has a resolved threshold and a finite current value, computed with
/// the same rules (including the CPU Tjmax-by-design carve-out) that drive that sensor's concern,
/// if any. This lets the UI color a tile/gauge exactly like the health panel instead of
/// re-deriving a possibly-contradictory level from a bare value/threshold comparison
/// (docs/SPEC.md analysis rule 2: a 9800X3D at 95 C holding clocks is WATCH, not CRITICAL).</summary>
public sealed record AnalysisResult(
    HealthStatus Status,
    string? Reason,
    string Headline,
    string Phase,
    IReadOnlyList<Concern> Concerns,
    IReadOnlyList<Trend> Trends,
    IReadOnlyDictionary<string, string> SensorLevels);
