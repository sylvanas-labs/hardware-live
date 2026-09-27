using System.Net;

namespace HardwareLive.Tests;

[Collection(RunningServerCollection.Name)]
public sealed class RouteMatrixTests(RunningServerFixture fixture)
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD", "TRACE"];

    public static IEnumerable<object[]> KnownMethodPathCases()
    {
        var allowedMethods = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["/"] = ["GET"],
            ["/api/snapshot"] = ["GET"],
            ["/api/meta"] = ["GET"],
            ["/api/health"] = ["GET"],
            ["/api/notes"] = ["GET"],
            ["/api/settings"] = ["GET", "PUT"],
            ["/app.css"] = ["GET"],
            ["/js/app.js"] = ["GET"],
            ["/js/logic.js"] = ["GET"],
            ["/js/dom.js"] = ["GET"],
            ["/js/api.js"] = ["GET"],
            ["/js/widgets.js"] = ["GET"],
            ["/js/dragdrop.js"] = ["GET"],
            ["/js/picker.js"] = ["GET"],
            ["/api/layouts"] = ["GET", "POST"],
            ["/api/layouts/matrix_item"] = ["PUT", "DELETE"],
            ["/api/layouts/import"] = ["POST"],
        };

        foreach (var (path, allowed) in allowedMethods)
        {
            foreach (var method in Methods)
            {
                yield return [path, method, allowed.Contains(method, StringComparer.Ordinal)];
            }
        }
    }

    public static IEnumerable<object[]> UnknownMethodPathCases()
    {
        foreach (var method in Methods)
        {
            yield return [method];
        }
    }

    [Theory]
    [MemberData(nameof(KnownMethodPathCases))]
    public async Task KnownPathsAllowOnlyTheirExactMethods(string path, string methodName, bool allowed)
    {
        using var request = BuildRequest(path, methodName, allowed);
        using var response = await fixture.Host.Client.SendAsync(request);

        if (allowed)
        {
            Assert.True(response.IsSuccessStatusCode, $"Expected {methodName} {path} to succeed, received {(int)response.StatusCode}.");
        }
        else
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        AssertNoServerOrCorsHeaders(response);
    }

    [Theory]
    [MemberData(nameof(UnknownMethodPathCases))]
    public async Task UnknownPathReturnsNotFoundForEveryMethod(string methodName)
    {
        using var request = new HttpRequestMessage(new HttpMethod(methodName), "/not-a-route");
        using var response = await fixture.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoServerOrCorsHeaders(response);
    }

    internal static void AssertNoServerOrCorsHeaders(HttpResponseMessage response)
    {
        Assert.False(response.Headers.Contains("Server"));
        Assert.DoesNotContain(
            response.Headers.Concat(response.Content.Headers),
            header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private HttpRequestMessage BuildRequest(string path, string methodName, bool allowed)
    {
        var method = new HttpMethod(methodName);
        if (!allowed || method == HttpMethod.Get)
        {
            return new HttpRequestMessage(method, path);
        }

        var json = path switch
        {
            "/api/layouts" => """{"id":"matrix_create","name":"Matrix create","widgets":[]}""",
            "/api/layouts/matrix_item" when method == HttpMethod.Put =>
                """{"id":"matrix_item","name":"Matrix replace","widgets":[]}""",
            "/api/layouts/import" =>
                """{"layouts":[{"id":"matrix_import","name":"Matrix import","widgets":[]}]}""",
            "/api/settings" => """{"activePresetId":"builtin-overview"}""",
            _ => null,
        };

        return TestLayouts.AuthenticatedWrite(method, path, fixture.Host.Server.Token, json);
    }
}
