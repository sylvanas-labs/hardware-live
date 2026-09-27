using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class PortConfigurationTests
{
    [Fact]
    public void MissingConfigAndPortArgumentUseDefault()
    {
        var missingConfig = Path.Combine(Path.GetTempPath(), $"hardware-live-missing-{Guid.NewGuid():N}.json");

        var port = PortConfiguration.Resolve([], missingConfig);

        Assert.Equal(HardwareLiveServer.DefaultPort, port);
    }

    [Fact]
    public void ConfigFileCanSetEphemeralPort()
    {
        var configPath = WriteConfig("""{"port":0}""");
        try
        {
            Assert.Equal(0, PortConfiguration.Resolve([], configPath));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{"port":"eight"}""")]
    [InlineData("""{"port":70000}""")]
    [InlineData("""{"port":-1}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("")]
    public void BrokenConfigFileNeverCrashesAndFallsBackToDefault(string content)
    {
        // config.json is user-editable in %LOCALAPPDATA%; a typo must not stop the app.
        var configPath = WriteConfig(content);
        try
        {
            Assert.Equal(HardwareLiveServer.DefaultPort, PortConfiguration.Resolve([], configPath));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void PortArgumentOverridesConfigAndUrlsArgumentIsIgnored()
    {
        var configPath = WriteConfig("""{"port":8790}""");
        try
        {
            var args = new[] { "--urls", "http://0.0.0.0:12345", "--port", "0" };

            Assert.Equal(0, PortConfiguration.Resolve(args, configPath));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("not-a-number")]
    public void InvalidPortArgumentIsRejected(string value)
    {
        var missingConfig = Path.Combine(Path.GetTempPath(), $"hardware-live-missing-{Guid.NewGuid():N}.json");

        Assert.ThrowsAny<ArgumentException>(() => PortConfiguration.Resolve(["--port", value], missingConfig));
    }

    private static string WriteConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hardware-live-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
