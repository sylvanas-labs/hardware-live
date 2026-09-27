using System.Net;
using System.Text.Json;
using HardwareLive.Core;
using HardwareLive.Tests.Classification;

namespace HardwareLive.Tests;

public sealed class ClassifierApiTests
{
    [Fact]
    public async Task MetaContainsRolesForTheRealFixture()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var store = new TelemetryStore();
        store.Add(frame);
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/meta");
        using var meta = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roles = meta.RootElement.GetProperty("roles");
        Assert.True(roles.GetArrayLength() > 0);
        Assert.Contains(
            roles.EnumerateArray(),
            role => role.GetProperty("role").GetString() == "cpu.temp.control");
        Assert.Equal("/amdcpu/0", meta.RootElement.GetProperty("primaryCpuId").GetString());
        Assert.Equal("/gpu-nvidia/0", meta.RootElement.GetProperty("primaryGpuId").GetString());
        Assert.Empty(meta.RootElement.GetProperty("missingMandatory").EnumerateArray());
    }

    [Fact]
    public async Task HealthReasonReportsUnmappedRolesWhenPawnIoIsMissing()
    {
        var frame = ClassificationFixtures.LoadFrame("synthetic-pawnio-missing_amd");
        var store = new TelemetryStore();
        store.Add(frame);
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("unmapped: cpu.temp.control", health.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HealthReasonIsAnalysisNotImplementedWhenAllMandatoryRolesAreMapped()
    {
        var frame = ClassificationFixtures.LoadFrame("amd-9800x3d_nvidia-5090_desktop");
        var store = new TelemetryStore();
        store.Add(frame);
        await using var host = await ServerTestHost.StartAsync(store);

        using var response = await host.Client.GetAsync("/api/health");
        using var health = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("analysis not implemented", health.RootElement.GetProperty("reason").GetString());
    }
}
