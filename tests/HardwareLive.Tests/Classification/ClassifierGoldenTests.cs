using HardwareLive.Core.Classification;

namespace HardwareLive.Tests.Classification;

public sealed class ClassifierGoldenTests
{
    public static IEnumerable<object[]> Fixtures =>
        ClassificationFixtures.AllFixtureNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void ClassifierMatchesGoldenFile(string fixtureName)
    {
        var frame = ClassificationFixtures.LoadFrame(fixtureName);
        var golden = ClassificationFixtures.LoadGolden(fixtureName);

        var result = SensorClassifier.Classify(frame);

        var actualRoles = ClassificationFixtures.ActualRoles(result);
        var expectedRoles = golden.Roles
            .OrderBy(r => r.SensorId, StringComparer.Ordinal)
            .ThenBy(r => r.Role, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedRoles, actualRoles);

        var actualLimits = ClassificationFixtures.ActualLimits(result);
        var expectedLimits = golden.Limits
            .OrderBy(l => l.SensorId, StringComparer.Ordinal)
            .ThenBy(l => l.Kind, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedLimits, actualLimits);

        Assert.Equal(golden.PrimaryCpuId, result.PrimaryCpuId);
        Assert.Equal(golden.PrimaryGpuId, result.PrimaryGpuId);
        Assert.Equal(golden.MissingMandatory, result.MissingMandatory);
    }
}
