using System.Net;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Protocol;

namespace HardwareLive.Tests;

public sealed class TelemetryApiTests
{
    [Fact]
    public async Task SnapshotCanLimitHistoryToRequestedIds()
    {
        var store = new TelemetryStore();
        store.Add(Frame(1, 40, 50));
        store.Add(Frame(2, 41, null));
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/snapshot?ids=/cpu/0/temp/0");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var history = json.RootElement.GetProperty("history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var selected = Assert.Single(history.EnumerateObject());
        Assert.Equal("/cpu/0/temp/0", selected.Name);
        Assert.Equal(new[] { 40f, 41f }, selected.Value.EnumerateArray().Select(item => item.GetSingle()));
        Assert.Equal(2, json.RootElement.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public async Task SnapshotRejectsMoreThanTwoHundredHistoryIds()
    {
        await using var host = await ServerTestHost.StartAsync(new TelemetryStore());
        var ids = string.Join(',', Enumerable.Range(0, 201).Select(index => $"sensor-{index}"));

        using var response = await host.Client.GetAsync($"/api/snapshot?ids={ids}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MetaAndHealthUseLatestFreshFrame()
    {
        var store = new TelemetryStore();
        store.Add(Frame(9, 42, 52));
        await using var host = await ServerTestHost.StartAsync(store);

        using var metaResponse = await host.Client.GetAsync("/api/meta");
        using var meta = await JsonDocument.ParseAsync(await metaResponse.Content.ReadAsStreamAsync());
        using var healthResponse = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await healthResponse.Content.ReadAsStreamAsync());

        Assert.Equal("/cpu/0", meta.RootElement.GetProperty("hardware")[0].GetProperty("id").GetString());
        Assert.Equal("/cpu/0/temp/0", meta.RootElement.GetProperty("sensors")[0].GetProperty("id").GetString());
        Assert.True(meta.RootElement.GetProperty("pawnIoInstalled").GetBoolean());
        Assert.True(meta.RootElement.GetProperty("elevated").GetBoolean());
        Assert.Equal("0.9.6.0", meta.RootElement.GetProperty("lhmVersion").GetString());
        Assert.Equal("UNKNOWN", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("unmapped: cpu.temp.control", health.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HealthReportsAFrameOlderThanFiveSecondsAsStale()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new TelemetryStore(clock);
        store.Add(Frame(1, 42, 52));
        clock.Advance(TimeSpan.FromSeconds(6));
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("sampler stale", health.RootElement.GetProperty("reason").GetString());
    }

    private static SensorFrame Frame(long sequence, float? cpu, float? gpu) =>
        new(
            SensorFrame.CurrentVersion,
            sequence,
            1000 + sequence,
            PawnIoInstalled: true,
            Elevated: true,
            LhmVersion: "0.9.6.0",
            Hardware: [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            Sensors:
            [
                new SensorReading("/cpu/0/temp/0", "/cpu/0", "CPU", "Temperature", cpu, null, null),
                new SensorReading("/gpu/0/temp/0", "/gpu/0", "GPU", "Temperature", gpu, null, null),
            ]);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
