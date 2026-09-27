using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HardwareLive.Core;

public sealed class HardwareLiveServer : IAsyncDisposable
{
    public const int DefaultPort = 8790;
    public const int MaximumRequestBodySize = 256 * 1024;

    private readonly WebApplication _application;
    private bool _started;

    private HardwareLiveServer(WebApplication application, string token)
    {
        _application = application;
        Token = token;
    }

    public string Token { get; }

    public Uri BoundAddress { get; private set; } = null!;

    public IReadOnlyList<Uri> BoundAddresses { get; private set; } = [];

    public static HardwareLiveServer Create(int port = DefaultPort)
    {
        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(HardwareLiveServer).Assembly.GetName().Name,
        });

        // The server is deliberately configuration-independent: neither environment
        // variables nor command-line URL settings are allowed to add listeners.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls([]);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaximumRequestBodySize;
            options.Listen(IPAddress.Loopback, port);
        });
        builder.Logging.ClearProviders();

        var application = builder.Build();

        // This must remain the first middleware in the pipeline.
        application.Use(HostHeaderGuard);
        var router = new RequestRouter(tokenBytes, token, new InMemoryLayoutStore());
        application.Run(router.HandleAsync);

        return new HardwareLiveServer(application, token);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            throw new InvalidOperationException("The server has already been started.");
        }

        await _application.StartAsync(cancellationToken);
        _started = true;

        var addressesFeature = _application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        var addresses = addressesFeature?.Addresses.Select(address => new Uri(address)).ToArray() ?? [];

        if (addresses.Length != 1 || !IPAddress.TryParse(addresses[0].Host, out var address) || !IPAddress.Loopback.Equals(address))
        {
            await _application.StopAsync(CancellationToken.None);
            throw new InvalidOperationException("Kestrel did not bind exactly one IPv4 loopback address.");
        }

        BoundAddresses = addresses;
        BoundAddress = addresses[0];
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        _application.WaitForShutdownAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await _application.StopAsync(CancellationToken.None);
        }

        await _application.DisposeAsync();
    }

    private static async Task HostHeaderGuard(HttpContext context, RequestDelegate next)
    {
        var host = context.Request.Host;
        var allowedName = host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        var allowedPort = host.Port.HasValue && host.Port.Value == context.Connection.LocalPort;

        if (!allowedName || !allowedPort)
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            context.Response.ContentLength = 0;
            return;
        }

        await next(context);
    }

}
