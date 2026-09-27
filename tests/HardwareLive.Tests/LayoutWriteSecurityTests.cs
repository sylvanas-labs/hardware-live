using System.Net;
using System.Text;
using System.Text.Json;

namespace HardwareLive.Tests;

[Collection(RunningServerCollection.Name)]
public sealed class LayoutWriteSecurityTests(RunningServerFixture fixture)
{
    public static IEnumerable<object[]> ProtectedWriteRoutes()
    {
        yield return ["POST", "/api/layouts", TestLayouts.Valid];
        yield return ["PUT", "/api/layouts/protected", """{"id":"protected","name":"Protected","widgets":[]}"""];
        yield return ["DELETE", "/api/layouts/protected", null!];
        yield return ["POST", "/api/layouts/import", """{"layouts":[]}"""];
    }

    public static IEnumerable<object[]> InvalidLayoutBodies()
    {
        yield return ["malformed JSON", "{"];
        yield return ["non-object layout", "[]"];
        yield return ["missing id", """{"name":"Name","widgets":[]}"""];
        yield return ["non-string id", """{"id":1,"name":"Name","widgets":[]}"""];
        yield return ["empty id", """{"id":"","name":"Name","widgets":[]}"""];
        yield return ["long id", JsonSerializer.Serialize(new { id = new string('a', 65), name = "Name", widgets = Array.Empty<object>() })];
        yield return ["invalid id characters", """{"id":"not valid","name":"Name","widgets":[]}"""];
        yield return ["reserved id import", """{"id":"import","name":"Name","widgets":[]}"""];
        yield return ["reserved id Import (case)", """{"id":"Import","name":"Name","widgets":[]}"""];
        yield return ["missing name", """{"id":"valid","widgets":[]}"""];
        yield return ["non-string name", """{"id":"valid","name":1,"widgets":[]}"""];
        yield return ["empty name", """{"id":"valid","name":"","widgets":[]}"""];
        yield return ["long name", JsonSerializer.Serialize(new { id = "valid", name = new string('a', 101), widgets = Array.Empty<object>() })];
        yield return ["missing widgets", """{"id":"valid","name":"Name"}"""];
        yield return ["non-array widgets", """{"id":"valid","name":"Name","widgets":{}}"""];
        yield return ["too many widgets", LayoutWithWidgetCount(501)];
        yield return ["non-object widget", """{"id":"valid","name":"Name","widgets":[1]}"""];
        yield return ["missing kind", """{"id":"valid","name":"Name","widgets":[{"size":"S","ref":{"role":"cpu.temp"}}]}"""];
        yield return ["invalid kind", """{"id":"valid","name":"Name","widgets":[{"kind":"map","size":"S","ref":{"role":"cpu.temp"}}]}"""];
        yield return ["missing size", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","ref":{"role":"cpu.temp"}}]}"""];
        yield return ["invalid size", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"XL","ref":{"role":"cpu.temp"}}]}"""];
        yield return ["missing ref", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S"}]}"""];
        yield return ["non-object ref", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":[]}]}"""];
        yield return ["empty ref", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{}}]}"""];
        yield return ["non-string role", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"role":1}}]}"""];
        yield return ["id without hw", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"id":"/cpu/0"}}]}"""];
        yield return ["hw without id", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"hw":"CPU"}}]}"""];
        yield return ["non-string exact id", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"id":1,"hw":"CPU"}}]}"""];
        yield return ["non-string hardware", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"id":"/cpu/0","hw":1}}]}"""];
        yield return ["unexpected layout property", """{"id":"valid","name":"Name","widgets":[],"extra":true}"""];
        yield return ["unexpected widget property", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"role":"cpu.temp"},"extra":true}]}"""];
        yield return ["unexpected ref property", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"role":"cpu.temp","extra":true}}]}"""];
        yield return ["chart with both ref and series", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","ref":{"role":"cpu.temp"},"series":[{"role":"gpu.temp"}]}]}"""];
        yield return ["chart with neither ref nor series", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L"}]}"""];
        yield return ["chart series empty", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","series":[]}]}"""];
        yield return ["chart series over twelve", ChartWithSeriesCount(13)];
        yield return ["chart series with invalid reference", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","series":[{}]}]}"""];
        yield return ["chart max not a number", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","series":[{"role":"cpu.temp"}],"max":"100"}]}"""];
        yield return ["chart max zero", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","series":[{"role":"cpu.temp"}],"max":0}]}"""];
        yield return ["chart max negative", """{"id":"valid","name":"Name","widgets":[{"kind":"chart","size":"L","series":[{"role":"cpu.temp"}],"max":-1}]}"""];
        yield return ["non-chart with series", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"role":"cpu.temp"},"series":[{"role":"gpu.temp"}]}]}"""];
        yield return ["non-chart with max", """{"id":"valid","name":"Name","widgets":[{"kind":"tile","size":"S","ref":{"role":"cpu.temp"},"max":100}]}"""];
    }

    private static string ChartWithSeriesCount(int count)
    {
        var series = Enumerable.Range(0, count).Select(i => new { role = $"role.{i}" });
        var widget = new { kind = "chart", size = "L", series };
        return JsonSerializer.Serialize(new { id = "valid", name = "Name", widgets = new[] { widget } });
    }

    public static IEnumerable<object[]> InvalidImportBodies()
    {
        yield return ["malformed import", "{"];
        yield return ["missing layouts", "{}"];
        yield return ["non-array layouts", """{"layouts":{}}"""];
        yield return ["too many layouts", ImportWithLayoutCount(101)];
        yield return ["invalid imported layout", """{"layouts":[{"id":"bad id","name":"Name","widgets":[]}]}"""];
        yield return ["reserved id in import", """{"layouts":[{"id":"import","name":"Name","widgets":[]}]}"""];
        yield return ["unexpected import property", """{"layouts":[],"extra":true}"""];
    }

    [Fact]
    public async Task WriteRequiresTokenAndAcceptsOnlyTheCorrectToken()
    {
        using var missingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/layouts")
        {
            Content = TestLayouts.Json(TestLayouts.Valid),
        };
        using var missingResponse = await fixture.Host.Client.SendAsync(missingRequest);
        Assert.Equal(HttpStatusCode.Forbidden, missingResponse.StatusCode);

        using var wrongRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", "wrong-token", TestLayouts.Valid);
        using var wrongResponse = await fixture.Host.Client.SendAsync(wrongRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongResponse.StatusCode);

        const string validBody = """{"id":"auth_success","name":"Authorized","widgets":[]}""";
        using var rightRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", fixture.Host.Server.Token, validBody);
        using var rightResponse = await fixture.Host.Client.SendAsync(rightRequest);
        Assert.Equal(HttpStatusCode.Created, rightResponse.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedWriteRoutes))]
    public async Task EveryWriteRouteRejectsMissingAndWrongTokens(string methodName, string path, string? body)
    {
        var method = new HttpMethod(methodName);
        using var missingRequest = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            missingRequest.Content = TestLayouts.Json(body);
        }

        using var missingResponse = await fixture.Host.Client.SendAsync(missingRequest);
        Assert.Equal(HttpStatusCode.Forbidden, missingResponse.StatusCode);

        using var wrongRequest = TestLayouts.AuthenticatedWrite(method, path, "wrong-token", body);
        using var wrongResponse = await fixture.Host.Client.SendAsync(wrongRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrongResponse.StatusCode);
    }

    [Fact]
    public async Task ValidWidgetKindsSizesAndReferenceFormsAreAccepted()
    {
        const string body = """
            {
              "id": "all_widgets",
              "name": "All widgets",
              "widgets": [
                { "kind": "tile", "size": "S", "ref": { "role": "cpu.temp" } },
                { "kind": "chart", "size": "M", "ref": { "id": "/cpu/0", "hw": "CPU" } },
                { "kind": "chart", "size": "L", "series": [ { "role": "cpu.load.total" }, { "role": "gpu.load.core" } ], "max": 100 },
                { "kind": "gauge", "size": "L", "ref": { "id": "/gpu/0", "hw": "GPU", "role": "gpu.temp" } },
                { "kind": "analysis", "size": "M", "ref": { "role": "analysis.health" } },
                { "kind": "notes", "size": "S", "ref": { "role": "notes" } }
              ]
            }
            """;
        using var request = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", fixture.Host.Server.Token, body);
        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task OversizeBodyIsRejected()
    {
        using var request = TestLayouts.AuthenticatedWrite(
            HttpMethod.Post,
            "/api/layouts",
            fixture.Host.Server.Token,
            new string('x', 257 * 1024));

        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        using var request = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", fixture.Host.Server.Token);
        request.Content = new StringContent(TestLayouts.Valid, Encoding.UTF8, "text/plain");

        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidLayoutBodies))]
    public async Task InvalidLayoutSchemaIsRejected(string _, string body)
    {
        using var request = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", fixture.Host.Server.Token, body);
        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidImportBodies))]
    public async Task InvalidImportSchemaIsRejected(string _, string body)
    {
        using var request = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts/import", fixture.Host.Server.Token, body);
        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CrossOriginWriteWithoutTokenAndPreflightAreRejectedWithoutCorsHeaders()
    {
        var origin = $"http://127.0.0.1:{DifferentPort(fixture.Host.Port)}";
        using var write = new HttpRequestMessage(HttpMethod.Post, "/api/layouts")
        {
            Content = TestLayouts.Json(TestLayouts.Valid),
        };
        write.Headers.Add("Origin", origin);
        using var writeResponse = await fixture.Host.Client.SendAsync(write);

        Assert.Equal(HttpStatusCode.Forbidden, writeResponse.StatusCode);
        RouteMatrixTests.AssertNoServerOrCorsHeaders(writeResponse);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/layouts");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "X-HL-Token, Content-Type");
        using var preflightResponse = await fixture.Host.Client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, preflightResponse.StatusCode);
        RouteMatrixTests.AssertNoServerOrCorsHeaders(preflightResponse);
    }

    [Fact]
    public async Task LayoutWritesMutateTheInMemoryStore()
    {
        const string create = """{"id":"store_case","name":"Created","widgets":[]}""";
        using var createRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts", fixture.Host.Server.Token, create);
        using var createResponse = await fixture.Host.Client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        const string replace = """{"id":"store_case","name":"Replaced","widgets":[]}""";
        using var replaceRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Put, "/api/layouts/store_case", fixture.Host.Server.Token, replace);
        using var replaceResponse = await fixture.Host.Client.SendAsync(replaceRequest);
        Assert.Equal(HttpStatusCode.OK, replaceResponse.StatusCode);

        const string import = """{"layouts":[{"id":"import_one","name":"One","widgets":[]},{"id":"import_two","name":"Two","widgets":[]}]}""";
        using var importRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Post, "/api/layouts/import", fixture.Host.Server.Token, import);
        using var importResponse = await fixture.Host.Client.SendAsync(importRequest);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

        using var listResponse = await fixture.Host.Client.GetAsync("/api/layouts");
        var listed = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Replaced\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"import_one\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"import_two\"", listed, StringComparison.Ordinal);

        using var deleteRequest = TestLayouts.AuthenticatedWrite(HttpMethod.Delete, "/api/layouts/store_case", fixture.Host.Server.Token);
        using var deleteResponse = await fixture.Host.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var finalListResponse = await fixture.Host.Client.GetAsync("/api/layouts");
        var finalList = await finalListResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"id\":\"store_case\"", finalList, StringComparison.Ordinal);
    }

    private static int DifferentPort(int port) => port == ushort.MaxValue ? port - 1 : port + 1;

    private static string LayoutWithWidgetCount(int count)
    {
        var widget = new { kind = "tile", size = "S", @ref = new { role = "cpu.temp" } };
        return JsonSerializer.Serialize(new { id = "valid", name = "Name", widgets = Enumerable.Repeat(widget, count) });
    }

    private static string ImportWithLayoutCount(int count)
    {
        var layout = new { id = "valid", name = "Name", widgets = Array.Empty<object>() };
        return JsonSerializer.Serialize(new { layouts = Enumerable.Repeat(layout, count) });
    }
}
