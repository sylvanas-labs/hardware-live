namespace HardwareLive.Tests;

public sealed class RunningServerFixture : IAsyncLifetime
{
    internal ServerTestHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await ServerTestHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RunningServerCollection : ICollectionFixture<RunningServerFixture>
{
    public const string Name = "Running loopback server";
}
