using HardwareLive.Core.Profiles;

namespace HardwareLive.Tests.Profiles;

public sealed class ThresholdProfilesTests
{
    [Fact]
    public void EmbeddedResourceLoadsWithTheExactSpecEntries()
    {
        var table = ThresholdProfiles.Table;

        var exact9800X3D = Assert.Single(table.Cpu, e => e.Name == "AMD Ryzen 7 9800X3D");
        Assert.Equal("vendor", exact9800X3D.Confidence);
        Assert.Equal(
            "https://www.amd.com/en/products/processors/desktops/ryzen/9000-series/amd-ryzen-7-9800x3d.html",
            exact9800X3D.Source);
        var cpuLimit = Assert.Single(exact9800X3D.Limits);
        Assert.Equal(95, cpuLimit.Limit);

        var intel = Assert.Single(table.Cpu, e => e.Name.Contains("Intel", StringComparison.Ordinal));
        Assert.Equal(100, Assert.Single(intel.Limits).Limit);
        Assert.Equal("vendor", intel.Confidence);

        var nvidia = Assert.Single(table.Gpu, e => e.Name.Contains("NVIDIA", StringComparison.Ordinal));
        var nvidiaCore = Assert.Single(nvidia.Limits, l => l.Role == "gpu.temp.core");
        Assert.Equal(90, nvidiaCore.Limit);
        Assert.Equal("vendor", nvidiaCore.Confidence);
        Assert.Equal("https://www.nvidia.com/en-us/geforce/graphics-cards/50-series/rtx-5090/", nvidiaCore.Source);

        var amd = Assert.Single(table.Gpu, e => e.Name.Contains("AMD Radeon", StringComparison.Ordinal));
        var hotspot = Assert.Single(amd.Limits, l => l.Role == "gpu.temp.hotspot");
        Assert.Equal(100, hotspot.Watch);
        Assert.Equal(110, hotspot.Critical);
        Assert.Equal("secondary", hotspot.Confidence);

        var genericCpu = Assert.Single(table.Generic, l => l.Role == "cpu.temp.control");
        Assert.Equal(90, genericCpu.Limit);
        Assert.Equal("community", genericCpu.Confidence);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D")]
    [InlineData("AMD Ryzen 9 9950X3D")]
    [InlineData("Intel Core i7-13700K")]
    public void CpuPatternsMatchTheirIntendedNames(string name)
    {
        var table = ThresholdProfiles.Table;
        Assert.Contains(table.Cpu, e => System.Text.RegularExpressions.Regex.IsMatch(name, e.Pattern));
    }
}
