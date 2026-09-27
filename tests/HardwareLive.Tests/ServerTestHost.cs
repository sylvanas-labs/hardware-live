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

    public static async Task<ServerTestHost> StartAsync(
        ITelemetrySource? telemetry = null,
        TimeProvider? clock = null,
        string? notesDirectory = null)
    {
        var server = HardwareLiveServer.Create(new HardwareLiveServerOptions
        {
            Port = 0,
            Telemetry = telemetry ?? new TelemetryStore(),
            Clock = clock,
            NotesDirectory = notesDirectory,
        });
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
