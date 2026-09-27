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
    IReadOnlyList<string> MissingMandatory)
{
    public static readonly ClassificationResult Empty = new([], [], null, null, []);
}
