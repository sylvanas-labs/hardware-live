using HardwareLive.Core.Classification;
using HardwareLive.Core.Profiles;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests.Profiles;

public sealed class ThresholdResolverTests
{
    [Fact]
    public void RealFixtureResolvesDeviceLimitsProfilesAndGenericFallback()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);

        var resolution = ThresholdResolver.Resolve(frame, classification, UserThresholdConfig.Empty);

        // 990 PRO: WCTEMP 81 -> watch 76 / critical 81, origin device.
        var nvme = resolution.Thresholds["/nvme/2/temperature/0"];
        Assert.Equal(76, nvme.Watch);
        Assert.Equal(81, nvme.Critical);
        Assert.Equal(ThresholdOrigin.Device, nvme.Origin);

        // DIMM #1: high 55 / critical-high 85, both from the device.
        var dimm = resolution.Thresholds["/memory/dimm/1/temperature/0"];
        Assert.Equal(55, dimm.Watch);
        Assert.Equal(85, dimm.Critical);
        Assert.Equal(ThresholdOrigin.Device, dimm.Origin);

        // 9800X3D: exact vendor profile match, limit 95 -> watch 88 / critical 95.
        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(88, cpu.Watch);
        Assert.Equal(95, cpu.Critical);
        Assert.Equal(ThresholdOrigin.Profile, cpu.Origin);
        Assert.Equal(ThresholdConfidence.Vendor, cpu.Confidence);
        Assert.Equal("AMD Ryzen 7 9800X3D", resolution.CpuProfile);

        // RTX 5090: vendor profile match, limit 90 -> watch 83 / critical 90.
        var gpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.GpuTempCore);
        var gpu = resolution.Thresholds[gpuRole.SensorId];
        Assert.Equal(83, gpu.Watch);
        Assert.Equal(90, gpu.Critical);
        Assert.Equal(ThresholdOrigin.Profile, gpu.Origin);
        Assert.NotNull(resolution.GpuProfile);
    }

    [Fact]
    public void IntelDesktopFixtureGetsVendorLimitOfOneHundred()
    {
        var frame = ClassificationFixtures.LoadFrame("synthetic-intel-13700k_amd-7900xtx_desktop");
        var classification = SensorClassifier.Classify(frame);

        var resolution = ThresholdResolver.Resolve(frame, classification, UserThresholdConfig.Empty);

        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(93, cpu.Watch);
        Assert.Equal(100, cpu.Critical);
        Assert.Equal(ThresholdOrigin.Profile, cpu.Origin);
    }

    [Fact]
    public void IntelLaptopFourDigitModelMatchesIntelProfile()
    {
        // i7-1360P is 13th gen (Intel ARK: TJUNCTION 100 C); the model number has 4 digits,
        // unlike 5-digit desktop parts such as the i7-13700K.
        var frame = ClassificationFixtures.LoadFrame("synthetic-intel-1360p_laptop");
        var classification = SensorClassifier.Classify(frame);

        var resolution = ThresholdResolver.Resolve(frame, classification, UserThresholdConfig.Empty);

        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(93, cpu.Watch);
        Assert.Equal(100, cpu.Critical);
        Assert.Equal(ThresholdOrigin.Profile, cpu.Origin);
        Assert.NotNull(resolution.CpuProfile);
    }

    [Fact]
    public void UnmatchedCpuNameFallsBackToGenericNinety()
    {
        var original = ClassificationFixtures.LoadFrame("synthetic-intel-1360p_laptop");
        var frame = original with
        {
            Hardware = original.Hardware
                .Select(h => h.Id == "/intelcpu/0" ? h with { Name = "AMD Athlon 3000G" } : h)
                .ToList(),
        };
        var classification = SensorClassifier.Classify(frame);

        var resolution = ThresholdResolver.Resolve(frame, classification, UserThresholdConfig.Empty);

        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(83, cpu.Watch);
        Assert.Equal(90, cpu.Critical);
        Assert.Equal(ThresholdOrigin.Generic, cpu.Origin);
        Assert.Null(resolution.CpuProfile);
    }

    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", true)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-1360P", true)]
    [InlineData("Intel(R) Core(TM) i9-14900K", true)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", true)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", false)]
    [InlineData("Intel(R) Core(TM) i7-1165G7", false)]
    public void IntelProfilePatternCoversThirteenthAndFourteenthGenOnly(string cpuName, bool expected)
    {
        var intel = Assert.Single(ThresholdProfiles.Table.Cpu, p => p.Name.StartsWith("Intel", StringComparison.Ordinal));

        Assert.Equal(expected, System.Text.RegularExpressions.Regex.IsMatch(cpuName, intel.Pattern));
    }

    [Fact]
    public void RoleOverrideBeatsDeviceAndProfile()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var config = ConfigWith("""{"thresholds":{"cpu.temp.control":{"watch":70,"critical":80}}}""");

        var resolution = ThresholdResolver.Resolve(frame, classification, config);

        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(70, cpu.Watch);
        Assert.Equal(80, cpu.Critical);
        Assert.Equal(ThresholdOrigin.Override, cpu.Origin);
    }

    [Fact]
    public void SensorIdOverrideBeatsRoleOverride()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var json = "{\"thresholds\":{\"cpu.temp.control\":{\"watch\":70,\"critical\":80},\"" +
            cpuRole.SensorId + "\":{\"watch\":60,\"critical\":65}}}";
        var config = ConfigWith(json);

        var resolution = ThresholdResolver.Resolve(frame, classification, config);

        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(60, cpu.Watch);
        Assert.Equal(65, cpu.Critical);
    }

    [Fact]
    public void InvalidConfigFallsBackToDefaultsAndFlagsInvalid()
    {
        var config = ConfigWith("not json");
        Assert.True(config.Invalid);

        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var classification = SensorClassifier.Classify(frame);
        var resolution = ThresholdResolver.Resolve(frame, classification, config);

        var cpuRole = Assert.Single(classification.Roles, r => r.Role == Roles.CpuTempControl);
        var cpu = resolution.Thresholds[cpuRole.SensorId];
        Assert.Equal(88, cpu.Watch);
        Assert.Equal(95, cpu.Critical);
    }

    [Fact]
    public void NoFrameResolvesToEmpty()
    {
        var resolution = ThresholdResolver.Resolve(null, ClassificationResult.Empty, UserThresholdConfig.Empty);

        Assert.Empty(resolution.Thresholds);
        Assert.Null(resolution.CpuProfile);
        Assert.Null(resolution.GpuProfile);
    }

    private static UserThresholdConfig ConfigWith(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hl-resolver-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            return UserThresholdConfig.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
