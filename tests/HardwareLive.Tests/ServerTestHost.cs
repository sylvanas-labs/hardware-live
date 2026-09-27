using HardwareLive.Core;

namespace HardwareLive.Tests;

internal sealed class ServerTestHost : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _ownedDirectories;

    private ServerTestHost(HardwareLiveServer server, IReadOnlyList<string> ownedDirectories)
    {
        Server = server;
        _ownedDirectories = ownedDirectories;
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
        string? notesDirectory = null,
        string? layoutsDirectory = null)
    {
        var ownedDirectories = new List<string>();
        if (notesDirectory is null)
        {
            notesDirectory = CreateTestDirectory("notes");
            ownedDirectories.Add(notesDirectory);
        }

        if (layoutsDirectory is null)
        {
            layoutsDirectory = CreateTestDirectory("layouts");
            ownedDirectories.Add(layoutsDirectory);
        }

        var server = HardwareLiveServer.Create(new HardwareLiveServerOptions
        {
            Port = 0,
            Telemetry = telemetry ?? new TelemetryStore(),
            Clock = clock,
            NotesDirectory = notesDirectory,
            LayoutsDirectory = layoutsDirectory,
        });
        await server.StartAsync();
        return new ServerTestHost(server, ownedDirectories);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
        foreach (var directory in _ownedDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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

    private static string CreateTestDirectory(string purpose)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hl-tests-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
