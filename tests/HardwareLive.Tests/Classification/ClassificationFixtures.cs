using System.Text.Json;
using HardwareLive.Core.Classification;
using HardwareLive.Protocol;

namespace HardwareLive.Tests.Classification;

internal static class ClassificationFixtures
{
    public static readonly string[] AllFixtureNames =
    [
        "amd-9800x3d_nvidia-5090_desktop",
        "synthetic-intel-13700k_amd-7900xtx_desktop",
        "synthetic-intel-1360p_laptop",
        "synthetic-pawnio-missing_amd",
        "synthetic-ambiguous",
    ];

    public static SensorFrame LoadFrame(string fixtureName)
    {
        var bytes = File.ReadAllBytes(FixturePath($"{fixtureName}.json"));
        return JsonSerializer.Deserialize<SensorFrame>(bytes, SensorFrameJson.Options)
            ?? throw new InvalidOperationException($"Fixture '{fixtureName}' deserialized to null.");
    }

    public static GoldenExpectation LoadGolden(string fixtureName)
    {
        var bytes = File.ReadAllBytes(FixturePath($"{fixtureName}.roles.json"));
        return JsonSerializer.Deserialize<GoldenExpectation>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Golden file for '{fixtureName}' deserialized to null.");
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);

    public sealed record GoldenRole(string SensorId, string Role);

    public sealed record GoldenLimit(string SensorId, string Kind);

    public sealed record GoldenExpectation(
        List<GoldenRole> Roles,
        List<GoldenLimit> Limits,
        string? PrimaryCpuId,
        string? PrimaryGpuId,
        List<string> MissingMandatory);

    public static List<GoldenRole> ActualRoles(ClassificationResult result) =>
        result.Roles
            .Select(r => new GoldenRole(r.SensorId, r.Role))
            .OrderBy(r => r.SensorId, StringComparer.Ordinal)
            .ThenBy(r => r.Role, StringComparer.Ordinal)
            .ToList();

    public static List<GoldenLimit> ActualLimits(ClassificationResult result) =>
        result.Limits
            .Select(l => new GoldenLimit(l.SensorId, l.Kind))
            .OrderBy(l => l.SensorId, StringComparer.Ordinal)
            .ThenBy(l => l.Kind, StringComparer.Ordinal)
            .ToList();
}
