using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class ConfigPathsTests
{
    [Fact]
    public void FallsBackToAppBaseWhenLocalAppDataFileIsAbsent()
    {
        var appBase = Path.Combine(Path.GetTempPath(), $"hl-appbase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appBase);
        try
        {
            var resolved = ConfigPaths.Resolve(appBase);

            Assert.Equal(Path.Combine(appBase, "config.json"), resolved);
        }
        finally
        {
            Directory.Delete(appBase, recursive: true);
        }
    }

    [Fact]
    public void PrefersLocalAppDataFileWhenPresent()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perUserDir = Path.Combine(localAppData, "HardwareLive");
        var perUserPath = Path.Combine(perUserDir, "config.json");
        var appBase = Path.Combine(Path.GetTempPath(), $"hl-appbase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appBase);
        Directory.CreateDirectory(perUserDir);
        var alreadyExisted = File.Exists(perUserPath);
        if (!alreadyExisted)
        {
            File.WriteAllText(perUserPath, "{}");
        }

        try
        {
            var resolved = ConfigPaths.Resolve(appBase);

            Assert.Equal(perUserPath, resolved);
        }
        finally
        {
            Directory.Delete(appBase, recursive: true);
            if (!alreadyExisted)
            {
                File.Delete(perUserPath);
            }
        }
    }
}
