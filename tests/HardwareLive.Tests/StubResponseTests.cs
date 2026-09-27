using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HardwareLive.Tests;

[Collection(RunningServerCollection.Name)]
public sealed class StubResponseTests(RunningServerFixture fixture)
{
    [Fact]
    public async Task RootIsTheHardwareLivePlaceholder()
    {
        using var response = await fixture.Host.Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Hardware Live</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotWithoutSamplerIsEmptyAndStale()
    {
        using var response = await fixture.Host.Client.GetAsync("/api/snapshot");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("timestampUnixMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("sequence").ValueKind);
        Assert.True(json.RootElement.GetProperty("stale").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("sensors").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("history").EnumerateObject());
    }

    [Fact]
    public async Task MetaWithoutSamplerIsEmpty()
    {
        using var response = await fixture.Host.Client.GetAsync("/api/meta");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(json.RootElement.GetProperty("hardware").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("sensors").EnumerateArray());
        Assert.False(json.RootElement.GetProperty("pawnIoInstalled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("elevated").GetBoolean());
        Assert.Equal(string.Empty, json.RootElement.GetProperty("lhmVersion").GetString());
    }

    [Fact]
    public async Task HealthReportsStartingWithinTheStartupWindow()
    {
        // Uses its own host (not the shared fixture) so the clock -- and therefore uptime --
        // is deterministic rather than however long earlier tests in this collection happened
        // to take.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var host = await ServerTestHost.StartAsync(clock: clock);

        using var response = await host.Client.GetAsync("/api/health");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("starting", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("uptimeSeconds").GetDouble());
    }

    [Fact]
    public async Task HealthExplainsThatSamplerIsNotRunningAfterTheStartupWindow()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var host = await ServerTestHost.StartAsync(clock: clock);
        clock.Advance(TimeSpan.FromSeconds(31));

        using var response = await host.Client.GetAsync("/api/health");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("sampler not running", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(31, json.RootElement.GetProperty("uptimeSeconds").GetDouble());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
