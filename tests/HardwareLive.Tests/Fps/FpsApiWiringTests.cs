using System.Net;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Core.Fps;
using HardwareLive.Protocol;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests.Fps;

/// <summary>docs/SPEC.md step7-fps items 5/6: <c>/api/snapshot</c>'s <c>fps</c> field and
/// <c>/api/health</c>'s FPS INFO concern (never WATCH/CRITICAL).</summary>
public sealed class FpsApiWiringTests
{
    private sealed class FakeFpsController(FpsSnapshotInfo current) : IFpsController
    {
        public FpsSnapshotInfo Current { get; } = current;

        public void SetEnabled(bool enabled) { }

        public void AddToDenylist(string processName) { }

        public void SetPinnedProcess(string? processName) { }
    }

    [Fact]
    public async Task SnapshotExposesFpsStatusAppAndPid()
    {
        var fps = new FakeFpsController(new FpsSnapshotInfo(FpsStatus.Tracking, "game.exe", 4242));
        await using var host = await ServerTestHost.StartAsync(fps: fps);

        using var response = await host.Client.GetAsync("/api/snapshot");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fpsElement = json.RootElement.GetProperty("fps");
        Assert.Equal("tracking", fpsElement.GetProperty("status").GetString());
        Assert.Equal("game.exe", fpsElement.GetProperty("app").GetString());
        Assert.Equal(4242, fpsElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task DisabledFpsAddsNoHealthConcernAndDoesNotAffectStatus()
    {
        var store = new TelemetryStore();
        store.Add(HealthyFrame());
        var fps = new FakeFpsController(FpsSnapshotInfo.Disabled);
        await using var host = await ServerTestHost.StartAsync(store, fps: fps);

        using var response = await host.Client.GetAsync("/api/health");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        var concerns = json.RootElement.GetProperty("concerns").EnumerateArray();
        Assert.DoesNotContain(concerns, c => c.GetProperty("role").GetString() == "fps.status");
    }

    [Theory]
    [InlineData(FpsStatus.NotInstalled, "PresentMon is not installed")]
    [InlineData(FpsStatus.NeedsPermission, "Performance Log Users")]
    [InlineData(FpsStatus.Unavailable, "FPS unavailable")]
    public async Task EnabledButUnusableFpsAddsAnInfoOnlyConcernNeverRaisingStatus(string status, string expectedFragment)
    {
        var store = new TelemetryStore();
        store.Add(HealthyFrame());
        var fps = new FakeFpsController(new FpsSnapshotInfo(status, null, null));
        await using var host = await ServerTestHost.StartAsync(store, fps: fps);

        using var response = await host.Client.GetAsync("/api/health");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        // Never WATCH/CRITICAL because of FPS alone (docs/SPEC.md step7-fps item 6).
        Assert.Equal("HEALTHY", json.RootElement.GetProperty("status").GetString());

        var concern = json.RootElement.GetProperty("concerns").EnumerateArray()
            .Single(c => c.GetProperty("role").GetString() == "fps.status");
        Assert.Equal("info", concern.GetProperty("level").GetString());
        Assert.Contains(expectedFragment, concern.GetProperty("message").GetString());
    }

    private static SensorFrame HealthyFrame() =>
        ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
}
