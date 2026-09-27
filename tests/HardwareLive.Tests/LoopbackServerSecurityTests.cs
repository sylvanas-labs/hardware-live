using System.Net;
using System.Net.Sockets;
using System.Text;
using HardwareLive.Core;

namespace HardwareLive.Tests;

public sealed class LoopbackServerSecurityTests
{
    public static IEnumerable<object[]> RejectedHostCases()
    {
        var paths = new[] { "/", "/api/snapshot", "/unknown" };
        var hostKinds = new[]
        {
            "evil",
            "wrong-port",
            "missing-port",
        };

        foreach (var path in paths)
        {
            foreach (var hostKind in hostKinds)
            {
                yield return [path, hostKind];
            }
        }
    }

    public static IEnumerable<object[]> MissingHostPaths()
    {
        yield return ["/"];
        yield return ["/api/snapshot"];
        yield return ["/unknown"];
    }

    [Fact]
    public async Task ExplicitLoopbackBindIgnoresAspNetCoreUrls()
    {
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:59432");

        try
        {
            await using var host = await ServerTestHost.StartAsync();

            var address = Assert.Single(host.Server.BoundAddresses);
            Assert.Equal(IPAddress.Loopback, IPAddress.Parse(address.Host));
            Assert.Equal(address, host.Server.BoundAddress);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
        }
    }

    [Theory]
    [MemberData(nameof(RejectedHostCases))]
    public async Task HostGuardRejectsInvalidHostOnEveryRouteType(string path, string hostKind)
    {
        await using var host = await ServerTestHost.StartAsync();
        var rejectedHost = hostKind switch
        {
            "evil" => $"evil.com:{host.Port}",
            "wrong-port" => $"127.0.0.1:{DifferentPort(host.Port)}",
            "missing-port" => "localhost",
            _ => throw new ArgumentOutOfRangeException(nameof(hostKind)),
        };

        using var response = await host.SendAsync(HttpMethod.Get, path, rejectedHost);

        Assert.Equal((HttpStatusCode)421, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(MissingHostPaths))]
    public async Task MissingHostIsRejectedWithoutResponseDetail(string path)
    {
        await using var host = await ServerTestHost.StartAsync();

        var response = await SendRawRequestAsync(host.Port, $"GET {path} HTTP/1.1\r\nConnection: close\r\n\r\n");

        Assert.True(
            response.StatusCode is 400 or 421,
            $"Expected status 400 or 421, received {response.StatusCode}.");
        Assert.Empty(response.Body);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("LOCALHOST")]
    public async Task HostGuardAcceptsAllowedHostsWithExactPort(string allowedHost)
    {
        await using var host = await ServerTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Get, "/", $"{allowedHost}:{host.Port}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokenIsStableForOneServerAndEmbeddedInThePage()
    {
        await using var host = await ServerTestHost.StartAsync();

        var firstToken = host.Server.Token;
        using var firstResponse = await host.Client.GetAsync("/");
        using var secondResponse = await host.Client.GetAsync("/");
        var firstPage = await firstResponse.Content.ReadAsStringAsync();
        var secondPage = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstToken, host.Server.Token);
        Assert.Contains($"<meta name=\"hl-token\" content=\"{firstToken}\">", firstPage, StringComparison.Ordinal);
        Assert.Contains($"<meta name=\"hl-token\" content=\"{firstToken}\">", secondPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenDiffersAcrossServerInstances()
    {
        await using var first = HardwareLiveServer.Create(0);
        await using var second = HardwareLiveServer.Create(0);

        Assert.NotEqual(first.Token, second.Token);
    }

    private static int DifferentPort(int port) => port == ushort.MaxValue ? port - 1 : port + 1;

    private static async Task<RawResponse> SendRawRequestAsync(int port, string request)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer.ToArray());
        var sections = response.Split("\r\n\r\n", 2, StringSplitOptions.None);
        var statusLine = sections[0].Split("\r\n", 2, StringSplitOptions.None)[0];
        var statusCode = int.Parse(statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        var body = sections.Length == 2 ? Encoding.ASCII.GetBytes(sections[1]) : [];
        return new RawResponse(statusCode, body);
    }

    private sealed record RawResponse(int StatusCode, byte[] Body);
}
