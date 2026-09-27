using HardwareLive.Core.Profiles;

namespace HardwareLive.Tests.Profiles;

public sealed class UserThresholdConfigTests
{
    [Fact]
    public void MissingFileIsEmptyAndNotInvalid()
    {
        var config = UserThresholdConfig.Load(Path.Combine(Path.GetTempPath(), $"hl-missing-{Guid.NewGuid():N}.json"));

        Assert.False(config.Invalid);
        Assert.False(config.TryGet("cpu.temp.control", out _));
    }

    [Fact]
    public void RoleAndSensorIdOverridesAreBothReadable()
    {
        var path = Write("""{"thresholds":{"cpu.temp.control":{"watch":80,"critical":90},"/nvme/2/temperature/0":{"critical":70}}}""");
        try
        {
            var config = UserThresholdConfig.Load(path);

            Assert.False(config.Invalid);
            Assert.True(config.TryGet("cpu.temp.control", out var role));
            Assert.Equal(80, role.Watch);
            Assert.Equal(90, role.Critical);
            Assert.True(config.TryGet("/nvme/2/temperature/0", out var bySensor));
            Assert.Null(bySensor.Watch);
            Assert.Equal(70, bySensor.Critical);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"thresholds":"not an object"}""")]
    [InlineData("""{"thresholds":{"cpu.temp.control":{"watch":0}}}""")]
    [InlineData("""{"thresholds":{"cpu.temp.control":{"watch":200}}}""")]
    [InlineData("""{"thresholds":{"cpu.temp.control":{"watch":90,"critical":80}}}""")]
    [InlineData("""{"thresholds":{"cpu.temp.control":{}}}""")]
    public void MalformedOrOutOfRangeConfigIsInvalidAndIgnored(string content)
    {
        var path = Write(content);
        try
        {
            var config = UserThresholdConfig.Load(path);

            Assert.True(config.Invalid);
            Assert.False(config.TryGet("cpu.temp.control", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingThresholdsSectionIsEmptyAndNotInvalid()
    {
        var path = Write("""{"port":8790}""");
        try
        {
            var config = UserThresholdConfig.Load(path);

            Assert.False(config.Invalid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hl-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
