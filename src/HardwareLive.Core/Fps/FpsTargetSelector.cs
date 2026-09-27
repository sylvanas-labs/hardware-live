namespace HardwareLive.Core.Fps;

/// <summary>The foreground window's process/eligibility signal (docs/SPEC.md r4.1), gathered
/// by an <see cref="IForegroundWindowProvider"/>. <see cref="CoversWholeMonitor"/> is the
/// required positive game signal -- present mode is corroboration only and is deliberately not
/// part of this snapshot, so nothing downstream can accidentally lean on it.</summary>
public sealed record ForegroundWindowSnapshot(
    int ProcessId,
    string ProcessName,
    int SessionId,
    bool CoversWholeMonitor);

/// <summary>Injectable seam over the Win32 foreground-window queries (docs/SPEC.md step7-fps
/// item 4: "Make the Win32 queries an injectable interface for tests"). Returns null when
/// there is no foreground window or any of the underlying queries fail.</summary>
public interface IForegroundWindowProvider
{
    ForegroundWindowSnapshot? GetForegroundWindow();
}

/// <summary>The current FPS target: a PID plus the process name shown as <c>fps.app</c>.</summary>
public sealed record FpsTarget(int ProcessId, string ProcessName, bool Pinned);

/// <summary>
/// Resolves the current FPS target per docs/SPEC.md r4.1 / step7-fps item 4. Pure aside from
/// the injected <see cref="IForegroundWindowProvider"/> and the <see cref="FpsAggregator"/>
/// it reads bucket/name data from -- no direct Win32 calls here, so this is fully unit
/// testable with fakes.
/// </summary>
public sealed class FpsTargetSelector
{
    private const int MinFps = 20;
    private const int ConsecutiveBuckets = 3;

    /// <summary>Built-in denylist (docs/SPEC.md r4.1), compared case-insensitively on the
    /// process name without the ".exe" suffix.</summary>
    public static readonly IReadOnlyList<string> BuiltInDenylist =
    [
        "explorer", "dwm",
        "msedge", "chrome", "firefox", "brave", "opera",
        "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "discord", "obs64", "nvcontainer",
        "vlc", "wmplayer", "video.ui", "mpc-hc64", "mpv", "potplayermini64",
        // Our own dashboard runs as an Edge "--app" window, i.e. process image msedge.exe --
        // already covered by the "msedge" browser entry above, so there is no separate name
        // to list here (docs/SPEC.md r4.1: "and our own Edge app window").
    ];

    private readonly IForegroundWindowProvider _foregroundWindows;
    private readonly FpsAggregator _aggregator;

    public FpsTargetSelector(IForegroundWindowProvider foregroundWindows, FpsAggregator aggregator)
    {
        _foregroundWindows = foregroundWindows ?? throw new ArgumentNullException(nameof(foregroundWindows));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
    }

    /// <summary>Selects the target as of <paramref name="uptoBucketKeyInclusive"/> (the latest
    /// complete bucket the caller has data for). Null means "no target" (FPS tiles show
    /// "–").</summary>
    public FpsTarget? SelectTarget(long uptoBucketKeyInclusive, FpsUserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.PinnedProcess))
        {
            var pinnedNormalized = NormalizeProcessName(config.PinnedProcess);
            foreach (var pid in _aggregator.GetTrackedPids())
            {
                var name = _aggregator.GetApplicationName(pid);
                if (name.Length > 0 && NormalizeProcessName(name) == pinnedNormalized)
                {
                    return new FpsTarget(pid, name, Pinned: true);
                }
            }

            // A configured pin that isn't currently presenting is "no target", not a silent
            // fallback to the foreground heuristic (the user asked for exactly this process).
            return null;
        }

        var foreground = _foregroundWindows.GetForegroundWindow();
        if (foreground is null || foreground.SessionId == 0 || !foreground.CoversWholeMonitor)
        {
            return null;
        }

        var normalizedForeground = NormalizeProcessName(foreground.ProcessName);
        if (BuiltInDenylist.Contains(normalizedForeground) ||
            config.DenylistExtra.Any(entry => NormalizeProcessName(entry) == normalizedForeground))
        {
            return null;
        }

        for (var i = 0; i < ConsecutiveBuckets; i++)
        {
            var bucketKey = uptoBucketKeyInclusive - (i * 1000L);
            if (_aggregator.GetFrameCount(foreground.ProcessId, bucketKey) < MinFps)
            {
                return null;
            }
        }

        return new FpsTarget(foreground.ProcessId, foreground.ProcessName, Pinned: false);
    }

    /// <summary>Case-insensitive comparison key: trims and strips a trailing ".exe" (docs/
    /// SPEC.md: "compare case-insensitively on process name without .exe").</summary>
    internal static string NormalizeProcessName(string name)
    {
        var trimmed = name.Trim();
        return (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed).ToLowerInvariant();
    }
}
