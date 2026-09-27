using System.Net;
using System.Text.Json;
using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class LayoutPresetApiTests
{
    [Fact]
    public async Task BuiltInsAreReturnedFirstAndEveryWriteRouteProtectsTheirIds()
    {
        using var directory = new TestDirectory();
        await using var host = await ServerTestHost.StartAsync(layoutsDirectory: directory.Path);

        using var listResponse = await host.Client.GetAsync("/api/layouts");
        using var list = await JsonDocument.ParseAsync(await listResponse.Content.ReadAsStreamAsync());
        Assert.Equal("builtin-overview", list.RootElement[0].GetProperty("id").GetString());
        Assert.True(list.RootElement[0].GetProperty("builtin").GetBoolean());

        var cases = new[]
        {
            (HttpMethod.Post, "/api/layouts", """{"id":"builtin-new","name":"No","widgets":[]}"""),
            (HttpMethod.Put, "/api/layouts/builtin-overview", """{"id":"builtin-overview","name":"No","widgets":[]}"""),
            (HttpMethod.Delete, "/api/layouts/builtin-overview", (string?)null),
            (HttpMethod.Post, "/api/layouts/import", """{"layouts":[{"id":"safe","name":"Safe","widgets":[]},{"id":"builtin-no","name":"No","widgets":[]}]}"""),
        };

        foreach (var (method, path, body) in cases)
        {
            using var request = TestLayouts.AuthenticatedWrite(method, path, host.Server.Token, body);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var finalResponse = await host.Client.GetAsync("/api/layouts");
        var finalJson = await finalResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\":\"safe\"", finalJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsAreStrictValidatedAndPersistAcrossServerRestart()
    {
        using var directory = new TestDirectory();
        await using (var host = await ServerTestHost.StartAsync(layoutsDirectory: directory.Path))
        {
            using var unknown = TestLayouts.AuthenticatedWrite(HttpMethod.Put, "/api/settings", host.Server.Token, """{"activePresetId":"missing"}""");
            using var unknownResponse = await host.Client.SendAsync(unknown);
            Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);

            using var invalidUnit = TestLayouts.AuthenticatedWrite(HttpMethod.Put, "/api/settings", host.Server.Token, """{"temperatureUnit":"K"}""");
            using var invalidUnitResponse = await host.Client.SendAsync(invalidUnit);
            Assert.Equal(HttpStatusCode.BadRequest, invalidUnitResponse.StatusCode);

            using var extra = TestLayouts.AuthenticatedWrite(HttpMethod.Put, "/api/settings", host.Server.Token, """{"temperatureUnit":"C","extra":true}""");
            using var extraResponse = await host.Client.SendAsync(extra);
            Assert.Equal(HttpStatusCode.BadRequest, extraResponse.StatusCode);

            using var valid = TestLayouts.AuthenticatedWrite(HttpMethod.Put, "/api/settings", host.Server.Token, """{"activePresetId":"builtin-cpu","temperatureUnit":"F"}""");
            using var validResponse = await host.Client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        }

        await using var restarted = await ServerTestHost.StartAsync(layoutsDirectory: directory.Path);
        using var getResponse = await restarted.Client.GetAsync("/api/settings");
        using var settings = await JsonDocument.ParseAsync(await getResponse.Content.ReadAsStreamAsync());
        Assert.Equal("builtin-cpu", settings.RootElement.GetProperty("activePresetId").GetString());
        Assert.Equal("F", settings.RootElement.GetProperty("temperatureUnit").GetString());
    }

    [Fact]
    public async Task ImportCollisionsAreRenamedWithStableSuffixes()
    {
        using var directory = new TestDirectory();
        await using var host = await ServerTestHost.StartAsync(layoutsDirectory: directory.Path);
        const string existing = """{"id":"desk","name":"Desk","widgets":[]}""";
        using (var request = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", host.Server.Token, existing))
        using (var response = await host.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        const string import = """{"layouts":[{"id":"desk","name":"First","widgets":[]},{"id":"desk","name":"Second","widgets":[]}]}""";
        using var importRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts/import", host.Server.Token, import);
        using var importResponse = await host.Client.SendAsync(importRequest);
        using var result = await JsonDocument.ParseAsync(await importResponse.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        Assert.Equal(2, result.RootElement.GetProperty("imported").GetInt32());
        var renames = result.RootElement.GetProperty("renamed");
        Assert.Equal("desk-2", renames[0].GetProperty("to").GetString());
        Assert.Equal("desk-3", renames[1].GetProperty("to").GetString());
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hardware-live-api-layout-tests-{Guid.NewGuid():N}");
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
