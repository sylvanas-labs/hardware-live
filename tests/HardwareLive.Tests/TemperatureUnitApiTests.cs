using System.Net;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Protocol;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests;

public sealed class TemperatureUnitApiTests
{
    private const string CpuControlId = "/amdcpu/0/temperature/2";

    [Fact]
    public async Task SameStateFormatsAbsoluteTemperatureAndTrendRateInStoredUnit()
    {
        using var directory = new TestDirectory();
        var telemetry = BuildTrendingTelemetry();
        await using var host = await ServerTestHost.StartAsync(telemetry, layoutsDirectory: directory.Path);

        await PutSettings(host, "C");
        using var cResponse = await host.Client.GetAsync("/api/health");
        using var cHealth = await JsonDocument.ParseAsync(await cResponse.Content.ReadAsStreamAsync());
        Assert.Contains("CPU 95.6 °C", cHealth.RootElement.GetProperty("headline").GetString(), StringComparison.Ordinal);
        var cTrend = FindCpuTrend(cHealth.RootElement);
        Assert.InRange(cTrend.GetProperty("slopePerMin").GetDouble(), 1.95, 2.05);
        Assert.Equal("°C/min", cTrend.GetProperty("unit").GetString());

        await PutSettings(host, "F");
        using var fResponse = await host.Client.GetAsync("/api/health");
        using var fHealth = await JsonDocument.ParseAsync(await fResponse.Content.ReadAsStreamAsync());
        Assert.Contains("CPU 204.1 °F", fHealth.RootElement.GetProperty("headline").GetString(), StringComparison.Ordinal);
        var fTrend = FindCpuTrend(fHealth.RootElement);
        Assert.InRange(fTrend.GetProperty("slopePerMin").GetDouble(), 3.55, 3.65);
        Assert.Equal("°F/min", fTrend.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task CorruptLayoutRecoveryIsAnInfoConcernEvenWithoutSamplerData()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "layouts.json"), "not json");
        await using var host = await ServerTestHost.StartAsync(layoutsDirectory: directory.Path);

        using var response = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var concern = Assert.Single(health.RootElement.GetProperty("concerns").EnumerateArray());
        Assert.Equal("info", concern.GetProperty("level").GetString());
        Assert.Equal("saved layouts could not be read; a backup was kept", concern.GetProperty("message").GetString());
    }

    private static async Task PutSettings(ServerTestHost host, string unit)
    {
        using var request = TestLayouts.AuthenticatedWrite(
            HttpMethod.Put,
            "/api/settings",
            host.Server.Token,
            $$"""{"temperatureUnit":"{{unit}}"}""");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static JsonElement FindCpuTrend(JsonElement health) =>
        Assert.Single(
            health.GetProperty("trends").EnumerateArray(),
            trend => trend.GetProperty("role").GetString() == "cpu.temp.control");

    private static TelemetryStore BuildTrendingTelemetry()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var store = new TelemetryStore();
        const double slopePerSample = 2.0 / 60.0;
        const int sampleCount = 60; // >= HealthAnalyzer's MinTrendSamples (60, 1 min at 1 Hz)
        var start = 95.6 - ((sampleCount - 1) * slopePerSample);
        for (var index = 0; index < sampleCount; index++)
        {
            var value = (float)(start + (index * slopePerSample));
            var sensors = frame.Sensors
                .Select(sensor => sensor.Id == CpuControlId ? sensor with { Value = value } : sensor)
                .ToArray();
            store.Add(frame with { Sequence = index + 1, TimestampUnixMs = index * 1000, Sensors = sensors });
        }

        return store;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hardware-live-unit-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
