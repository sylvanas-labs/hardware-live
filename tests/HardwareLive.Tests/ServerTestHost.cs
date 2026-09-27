using HardwareLive.Core;

namespace HardwareLive.Tests;

internal sealed class ServerTestHost : IAsyncDisposable
{
    private ServerTestHost(HardwareLiveServer server)
    {
        Server = server;
        Client = new HttpClient
        {
            BaseAddress = server.BoundAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public HardwareLiveServer Server { get; }

    public HttpClient Client { get; }

    public int Port => Server.BoundAddress.Port;

    public static async Task<ServerTestHost> StartAsync(ITelemetrySource? telemetry = null)
    {
        var server = telemetry is null
            ? HardwareLiveServer.Create(0)
            : HardwareLiveServer.Create(0, telemetry);
        await server.StartAsync();
        return new ServerTestHost(server);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? host = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return Client.SendAsync(request);
    }
}
