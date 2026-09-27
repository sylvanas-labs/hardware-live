namespace HardwareLive.App;

/// <summary>
/// Pure precedence rules for whether this process should open the dashboard window
/// (docs/SPEC.md Component 8 step 5, the "reopen" path). Kept separate from
/// <see cref="Program"/> so the precedence can be unit tested without spawning a browser,
/// binding a port, or touching the single-instance mutex.
/// </summary>
public static class WindowLaunchDecision
{
    /// <summary>
    /// This process is the primary instance (it acquired the single-instance mutex and is
    /// about to serve). Precedence, highest first: an explicit <c>--no-window</c> always wins
    /// (the user asked for no window); otherwise an explicit <c>--open</c> forces the window
    /// open; otherwise fall back to the <c>openWindowOnStart</c> config setting (default true).
    /// </summary>
    public static bool ShouldLaunchOnPrimaryInstance(bool noWindow, bool open, bool openWindowOnStartConfig)
    {
        if (noWindow)
        {
            return false;
        }

        return open || openWindowOnStartConfig;
    }

    /// <summary>
    /// Another instance already owns the mutex and is serving. This process must never start
    /// a second server (it would fail the port bind anyway) -- it only opens the browser, and
    /// only when explicitly asked with <c>--open</c>.
    /// </summary>
    public static bool ShouldLaunchOnSecondaryInstance(bool open) => open;
}
