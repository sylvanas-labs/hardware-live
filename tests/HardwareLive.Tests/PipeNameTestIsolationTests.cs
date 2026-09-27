using System.Security.Principal;
using HardwareLive.Core;
using HardwareLive.Protocol;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

/// <summary>
/// Regression coverage for the "All pipe instances are busy" test flakiness: any test that
/// constructs its own pipe server/client pair must use <see cref="PipeNames.ForTest"/>, never
/// the real per-user production name from <see cref="PipeNames.ForUser"/> (a real
/// <c>hl-sampler.exe</c> running on the same machine already owns that name and only one
/// server instance is ever allowed). <see cref="SamplerClient"/> and <see cref="SamplerService"/>
/// still default to the production name so a real deployment is unaffected.
/// </summary>
public sealed class PipeNameTestIsolationTests
{
    [Fact]
    public void NoTestSourceFileConstructsTheProductionPipeName()
    {
        var testRoot = Path.Combine(FindRepositoryRoot(), "tests", "HardwareLive.Tests");
        var thisFile = Path.Combine(testRoot, "PipeNameTestIsolationTests.cs");
        var files = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(thisFile), StringComparison.OrdinalIgnoreCase))
            {
                continue; // This file's own doc comment/assertions name PipeNames.ForUser.
            }

            var content = File.ReadAllText(file);
            Assert.DoesNotContain("PipeNames.ForUser(", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SamplerClientDefaultsToTheProductionPerUserPipeName()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var client = new SamplerClient(new TelemetryStore(), user: user);

        Assert.Equal(PipeNames.ForUser(user), client.PipeName);
    }

    [Fact]
    public void SamplerClientHonorsAnExplicitPipeNameOverride()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var testName = PipeNames.ForTest();
        var client = new SamplerClient(new TelemetryStore(), user: user, pipeName: testName);

        Assert.Equal(testName, client.PipeName);
    }

    [Fact]
    public void PipeNamesForTestNeverCollideAndNeverMatchTheProductionFormat()
    {
        var a = PipeNames.ForTest();
        var b = PipeNames.ForTest();

        Assert.NotEqual(a, b);
        Assert.StartsWith("HardwareLive.Test.", a, StringComparison.Ordinal);
        Assert.DoesNotContain("HardwareLive.Sampler.", a, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HardwareLive.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Hardware Live repository root.");
    }
}
