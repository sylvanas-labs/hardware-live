namespace HardwareLive.Core.Fps;

/// <summary>Injectable seam over a running child process (docs/SPEC.md step7-fps item "Runner
/// testability"), so <see cref="PresentMonRunner"/> is unit testable with a fake process
/// factory instead of always spawning a real PresentMon.exe.</summary>
public interface IManagedProcess : IAsyncDisposable
{
    int Id { get; }

    /// <summary>Reads one line of standard output, or null at end of stream.</summary>
    Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the process to exit and returns its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>The most recent standard-error text (bounded), used to detect an
    /// "access denied" exit (docs/SPEC.md: not in Performance Log Users).</summary>
    string StandardErrorTail { get; }

    void Kill();
}

/// <summary>Starts a PresentMon process. The production implementation is
/// <see cref="Win32PresentMonProcessLauncher"/>; tests supply a fake.</summary>
public interface IPresentMonProcessLauncher
{
    IManagedProcess Start(string exePath, string arguments);
}
