using System.Text.Json;
using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

public sealed class FpsUserConfigTests
{
    private static string TempConfigPath() => Path.Combine(Path.GetTempPath(), $"hl-fps-config-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFileYieldsDisabledDefaultsNotInvalid()
    {
        var config = FpsUserConfig.Load(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"));

        Assert.False(config.Enabled);
        Assert.Null(config.PinnedProcess);
        Assert.Empty(config.DenylistExtra);
        Assert.False(config.Invalid);
    }

    [Fact]
    public void MissingFpsSectionYieldsDefaultsNotInvalid()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """{"port":8790}""");
        try
        {
            var config = FpsUserConfig.Load(path);
            Assert.False(config.Enabled);
            Assert.False(config.Invalid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidFpsSectionParsesEveryField()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
            { "fps": { "enabled": true, "pinnedProcess": "MyGame.exe", "denylistExtra": ["Foo.exe", "bar"] } }
            """);
        try
        {
            var config = FpsUserConfig.Load(path);
            Assert.True(config.Enabled);
            Assert.Equal("MyGame.exe", config.PinnedProcess);
            Assert.Equal(["Foo.exe", "bar"], config.DenylistExtra);
            Assert.False(config.Invalid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("""{ "fps": "not-an-object" }""")]
    [InlineData("""{ "fps": { "enabled": "not-a-bool" } }""")]
    [InlineData("""{ "fps": { "denylistExtra": "not-an-array" } }""")]
    [InlineData("""{ "fps": { "denylistExtra": [1, 2] } }""")]
    [InlineData("not json at all")]
    public void MalformedFpsSectionNeverThrowsAndReportsInvalid(string content)
    {
        var path = TempConfigPath();
        File.WriteAllText(path, content);
        try
        {
            var config = FpsUserConfig.Load(path);
            Assert.True(config.Invalid);
            Assert.False(config.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UpdatePreservesUnrelatedTopLevelKeysAndWritesAtomically()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """{"port":8790,"thresholds":{"cpu.temp.control":{"watch":80}}}""");
        try
        {
            FpsUserConfig.Update(path, fps => fps["enabled"] = true);

            Assert.False(File.Exists(path + ".tmp"));
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(8790, document.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(80, document.RootElement.GetProperty("thresholds").GetProperty("cpu.temp.control").GetProperty("watch").GetInt32());
            Assert.True(document.RootElement.GetProperty("fps").GetProperty("enabled").GetBoolean());

            var reloaded = FpsUserConfig.Load(path);
            Assert.True(reloaded.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UpdatePreservesUnknownKeysWithinTheFpsSectionAcrossMultipleWrites()
    {
        var path = TempConfigPath();
        try
        {
            FpsUserConfig.Update(path, fps => fps["enabled"] = true);
            FpsUserConfig.Update(path, fps => fps["pinnedProcess"] = "game.exe");

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var fpsElement = document.RootElement.GetProperty("fps");
            Assert.True(fpsElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("game.exe", fpsElement.GetProperty("pinnedProcess").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UpdateCreatesTheFileWhenItDoesNotExist()
    {
        var path = TempConfigPath();
        try
        {
            Assert.False(File.Exists(path));
            FpsUserConfig.Update(path, fps => fps["enabled"] = true);

            Assert.True(File.Exists(path));
            var config = FpsUserConfig.Load(path);
            Assert.True(config.Enabled);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
