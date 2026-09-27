namespace HardwareLive.App;

/// <summary>
/// Wraps the named mutex that detects an already-running instance (docs/SPEC.md Component 8
/// step 5: <c>Local\HardwareLive.App</c>). <c>Local\</c> scopes it to the current session, so
/// it detects a duplicate launch by the same logged-on user -- not other users' sessions,
/// which never share a session-0-isolated mutex namespace anyway.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    public const string Name = @"Local\HardwareLive.App";

    public SingleInstanceGuard(string name = Name)
    {
        _mutex = new Mutex(initiallyOwned: true, name: name, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    /// <summary>True if this process acquired the mutex first, i.e. no other instance is
    /// currently serving.</summary>
    public bool IsPrimaryInstance { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only the owner may release; a secondary instance never acquired it.
        if (IsPrimaryInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
