using HardwareLive.App;

namespace HardwareLive.Tests;

/// <summary>
/// docs/SPEC.md Component 8 step 5: arg parsing, URL construction and the window-launch
/// precedence for the reopen path. <see cref="Program"/> itself (top-level statements) isn't
/// directly testable, so the logic lives in small pure classes exercised here.
/// </summary>
public sealed class AppLaunchTests
{
    [Fact]
    public void ParseRecognizesNoWindowAndOpen()
    {
        var parsed = AppArguments.Parse(["--no-window", "--open"]);

        Assert.True(parsed.NoWindow);
        Assert.True(parsed.Open);
    }

    [Fact]
    public void ParseIgnoresUnrelatedFlags()
    {
        var parsed = AppArguments.Parse(["--port", "9001"]);

        Assert.False(parsed.NoWindow);
        Assert.False(parsed.Open);
    }

    [Fact]
    public void ParseWithNoArgsIsAllFalse()
    {
        var parsed = AppArguments.Parse([]);

        Assert.False(parsed.NoWindow);
        Assert.False(parsed.Open);
    }

    [Theory]
    [InlineData(8790)]
    [InlineData(65535)]
    public void DashboardUrlUsesLoopbackLiteralNeverLocalhost(int port)
    {
        var url = DashboardUrl.Build(port);

        Assert.Equal($"http://127.0.0.1:{port}/", url);
        Assert.DoesNotContain("localhost", url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // noWindow, open, configDefault, expected
    [InlineData(true, true, true, false)] // --no-window always wins, even over --open
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)] // --open forces the window even if config says no
    [InlineData(false, false, true, true)] // falls through to config default
    [InlineData(false, false, false, false)]
    public void PrimaryInstancePrecedence(bool noWindow, bool open, bool configDefault, bool expected)
    {
        Assert.Equal(expected, WindowLaunchDecision.ShouldLaunchOnPrimaryInstance(noWindow, open, configDefault));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SecondaryInstanceOnlyOpensWhenAsked(bool open, bool expected)
    {
        Assert.Equal(expected, WindowLaunchDecision.ShouldLaunchOnSecondaryInstance(open));
    }

    [Fact]
    public void StartupConfigDefaultsTrueWhenFileMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

        Assert.True(AppStartupConfig.ReadOpenWindowOnStart(missingPath));
    }

    [Fact]
    public void StartupConfigReadsExplicitFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"openWindowOnStart\":false}");

        try
        {
            Assert.False(AppStartupConfig.ReadOpenWindowOnStart(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartupConfigReadsExplicitTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"openWindowOnStart\":true}");

        try
        {
            Assert.True(AppStartupConfig.ReadOpenWindowOnStart(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartupConfigDefaultsTrueOnMalformedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{not valid json");

        try
        {
            Assert.True(AppStartupConfig.ReadOpenWindowOnStart(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartupConfigDefaultsTrueWhenKeyAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, "{\"port\":9001}");

        try
        {
            Assert.True(AppStartupConfig.ReadOpenWindowOnStart(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SingleInstanceGuardFirstAcquireIsPrimary()
    {
        var name = @"Local\HardwareLiveTest." + Guid.NewGuid().ToString("N");
        using var guard = new SingleInstanceGuard(name);

        Assert.True(guard.IsPrimaryInstance);
    }

    [Fact]
    public void SingleInstanceGuardSecondAcquireIsSecondary()
    {
        var name = @"Local\HardwareLiveTest." + Guid.NewGuid().ToString("N");
        using var first = new SingleInstanceGuard(name);
        using var second = new SingleInstanceGuard(name);

        Assert.True(first.IsPrimaryInstance);
        Assert.False(second.IsPrimaryInstance);
    }
}
