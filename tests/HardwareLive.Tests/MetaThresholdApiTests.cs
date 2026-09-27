using System.Net;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Core.Profiles;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests;

public sealed class MetaThresholdApiTests
{
    [Fact]
    public async Task MetaExposesResolvedThresholdsAndMatchedProfilesForTheRealFixture()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var store = new TelemetryStore();
        store.Add(frame);
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/meta");
        using var meta = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var thresholds = meta.RootElement.GetProperty("thresholds");

        var nvme = thresholds.GetProperty("/nvme/2/temperature/0");
        Assert.Equal(76, nvme.GetProperty("watch").GetDouble());
        Assert.Equal(81, nvme.GetProperty("critical").GetDouble());
        Assert.Equal("device", nvme.GetProperty("origin").GetString());

        var dimm = thresholds.GetProperty("/memory/dimm/1/temperature/0");
        Assert.Equal(55, dimm.GetProperty("watch").GetDouble());
        Assert.Equal(85, dimm.GetProperty("critical").GetDouble());

        var profile = meta.RootElement.GetProperty("profile");
        Assert.Equal("AMD Ryzen 7 9800X3D", profile.GetProperty("cpu").GetString());
        Assert.NotEqual(JsonValueKind.Null, profile.GetProperty("gpu").ValueKind);
    }

    [Fact]
    public async Task HealthReturnsHealthyForTheRealFixtureFedThroughTheStore()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var store = new TelemetryStore();
        store.Add(frame);
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("HEALTHY", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("combined", health.RootElement.GetProperty("phase").GetString());
        Assert.Empty(health.RootElement.GetProperty("concerns").EnumerateArray());
    }

    [Fact]
    public async Task ConfigFileOverrideFlowsThroughToMetaEndToEnd()
    {
        // Smoke test of the full config.json -> UserThresholdConfig -> HardwareLiveServer ->
        // RequestRouter -> ThresholdResolver wiring, not just the resolver in isolation.
        var configPath = Path.Combine(Path.GetTempPath(), $"hl-e2e-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """{"thresholds":{"cpu.temp.control":{"watch":70,"critical":80}}}""");
        try
        {
            var config = UserThresholdConfig.Load(configPath);
            Assert.False(config.Invalid);

            var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
            var store = new TelemetryStore();
            store.Add(frame);
            await using var server = HardwareLiveServer.Create(0, store, config);
            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = server.BoundAddress };

            using var response = await client.GetAsync("/api/meta");
            using var meta = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

            var cpuSensorId = meta.RootElement.GetProperty("roles").EnumerateArray()
                .First(r => r.GetProperty("role").GetString() == "cpu.temp.control")
                .GetProperty("sensorId").GetString()!;
            var cpu = meta.RootElement.GetProperty("thresholds").GetProperty(cpuSensorId);

            Assert.Equal(70, cpu.GetProperty("watch").GetDouble());
            Assert.Equal(80, cpu.GetProperty("critical").GetDouble());
            Assert.Equal("override", cpu.GetProperty("origin").GetString());
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
