using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class ConfigPathsTests
{
    [Fact]
    public void FallsBackToAppBaseWhenPerUserFileIsAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hl-configpaths-{Guid.NewGuid():N}");
        var appBase = Path.Combine(root, "app");
        var perUser = Path.Combine(root, "peruser");
        Directory.CreateDirectory(appBase);
        Directory.CreateDirectory(perUser);
        try
        {
            Assert.Equal(Path.Combine(appBase, "config.json"), ConfigPaths.Resolve(appBase, perUser));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrefersPerUserFileWhenPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hl-configpaths-{Guid.NewGuid():N}");
        var appBase = Path.Combine(root, "app");
        var perUser = Path.Combine(root, "peruser");
        Directory.CreateDirectory(appBase);
        Directory.CreateDirectory(perUser);
        var perUserPath = Path.Combine(perUser, "config.json");
        File.WriteAllText(perUserPath, "{}");
        try
        {
            Assert.Equal(perUserPath, ConfigPaths.Resolve(appBase, perUser));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
