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

    [Theory]
    [InlineData("/api/snapshot")]
    [InlineData("/api/meta")]
    public async Task ReadStubReturnsStubStatus(string path)
    {
        using var response = await fixture.Host.Client.GetAsync(path);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stub", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthExplainsThatSamplerIsNotImplemented()
    {
        using var response = await fixture.Host.Client.GetAsync("/api/health");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("UNKNOWN", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("sampler not implemented", json.RootElement.GetProperty("reason").GetString());
    }
}
