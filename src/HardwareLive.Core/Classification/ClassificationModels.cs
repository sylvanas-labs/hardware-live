namespace HardwareLive.Core.Classification;

public enum Confidence
{
    High,
    Medium,
    Low,
}

public sealed record SensorRole(
    string SensorId,
    string Role,
    string HardwareId,
    Confidence Confidence,
    int? Instance = null);

public sealed record LimitSensor(
    string SensorId,
    string AppliesTo,
    string Kind);

public sealed record ClassificationResult(
    IReadOnlyList<SensorRole> Roles,
    IReadOnlyList<LimitSensor> Limits,
    string? PrimaryCpuId,
    string? PrimaryGpuId,
    IReadOnlyList<string> MissingMandatory,
    /// <summary>True when <see cref="Roles.CpuTempControl"/> was resolved via the AMD
    /// CCD fallback (no Tctl/Tdie-style sensor; the hottest "CCDn (Tdie)" reading was
    /// picked at classification time). Consumers (HealthAnalyzer) must then treat the
    /// control temperature as max(all cpu.temp.ccd sensors, including the one that won
    /// classification) evaluated fresh per sample, not pinned to whichever CCD happened
    /// to be hottest when the sensor-id set last changed.</summary>
    bool CpuControlIsCcdMax = false)
{
    public static readonly ClassificationResult Empty = new([], [], null, null, [], false);
}
