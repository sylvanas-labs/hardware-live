namespace HardwareLive.Tests;

public sealed class RunningServerFixture : IAsyncLifetime
{
    internal ServerTestHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Never the real %LOCALAPPDATA%\HardwareLive: tests must not depend on (or be
        // affected by) whatever notes.json a real machine happens to have.
        var notesDirectory = Path.Combine(Path.GetTempPath(), $"hl-tests-notes-{Guid.NewGuid():N}");
        Host = await ServerTestHost.StartAsync(notesDirectory: notesDirectory);
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
