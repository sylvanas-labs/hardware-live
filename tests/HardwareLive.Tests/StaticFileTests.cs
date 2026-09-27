using System.Net;

namespace HardwareLive.Tests;

[Collection(RunningServerCollection.Name)]
public sealed class StaticFileTests(RunningServerFixture fixture)
{
    public static IEnumerable<object[]> AllowListedFiles()
    {
        yield return ["/app.css", "text/css; charset=utf-8"];
        yield return ["/js/app.js", "text/javascript; charset=utf-8"];
        yield return ["/js/logic.js", "text/javascript; charset=utf-8"];
        yield return ["/js/dom.js", "text/javascript; charset=utf-8"];
        yield return ["/js/api.js", "text/javascript; charset=utf-8"];
        yield return ["/js/widgets.js", "text/javascript; charset=utf-8"];
        yield return ["/js/dragdrop.js", "text/javascript; charset=utf-8"];
        yield return ["/js/picker.js", "text/javascript; charset=utf-8"];
    }

    [Theory]
    [MemberData(nameof(AllowListedFiles))]
    public async Task AllowListedFilesAreServedWithTheRightContentTypeAndNoStoreCaching(string path, string contentType)
    {
        using var response = await fixture.Host.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.NotEmpty(body);
    }

    [Theory]
    [InlineData("/js/../../windows/win.ini")] // Normalizes to a non-root, still-unlisted path.
    [InlineData("/wwwroot/x")]
    [InlineData("/js/evil.js")]
    [InlineData("/js/")]
    [InlineData("/app.css.map")]
    public async Task UnknownStaticPathsAreNotFound(string path)
    {
        using var response = await fixture.Host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DotSegmentsCanNeverEscapeTheAllowListEvenWhenTheyNormalizeToAKnownPath()
    {
        // There is no filesystem access behind the allow-list (StaticFiles resolves an exact
        // path to an embedded resource, never a disk path), so a dot-segment that Uri
        // normalization collapses back onto an allowed path is harmless -- it just serves that
        // same allowed file, exactly as a plain request for it would.
        using var response = await fixture.Host.Client.GetAsync("/js/../app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css; charset=utf-8", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task PostToAStaticFileIsMethodNotAllowed()
    {
        using var response = await fixture.Host.Client.PostAsync("/app.css", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task RootHtmlHasTheStrictContentSecurityPolicyAndNosniff()
    {
        using var response = await fixture.Host.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("style-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }
}
