using HardwareLive.Protocol;

namespace HardwareLive.Core.Fps;

/// <summary>
/// Orchestrates FPS capture end to end (docs/SPEC.md Component 9 / step7-fps): owns the
/// config, the <see cref="PresentMonRunner"/>/<see cref="FpsAggregator"/>/
/// <see cref="FpsTargetSelector"/>, appends the synthetic <c>/fps/*</c> sensors to every real
/// sampler frame (<see cref="IFrameAugmentor"/>), and exposes the current status/app/pid for
/// <c>/api/snapshot</c>'s top-level <c>fps</c> field (<see cref="IFpsSnapshotProvider"/>).
/// Opt-in: the capture loop only runs while <c>fps.enabled</c> is true in config.json, polled
/// every <see cref="ConfigPollInterval"/> so a settings write takes effect without a restart.
/// </summary>
public sealed class FpsService : IFrameAugmentor, IFpsController
{
    private static readonly TimeSpan ConfigPollInterval = TimeSpan.FromSeconds(2);

    private readonly string _configPath;
    private readonly string _presentMonExePath;
    private readonly FpsAggregator _aggregator;
    private readonly FpsTargetSelector _targetSelector;
    private readonly PresentMonRunner _runner;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;

    private readonly object _sync = new();
    private FpsUserConfig _config;
    private FpsTarget? _currentTarget;

    public FpsService(
        string configPath,
        string presentMonExePath,
        IForegroundWindowProvider? foregroundWindows = null,
        IPresentMonProcessLauncher? launcher = null,
        TimeProvider? clock = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentMonExePath);
        _configPath = configPath;
        _presentMonExePath = presentMonExePath;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _aggregator = new FpsAggregator();
        _targetSelector = new FpsTargetSelector(foregroundWindows ?? new Win32ForegroundWindowProvider(), _aggregator);
        _runner = new PresentMonRunner(_presentMonExePath, _aggregator, launcher, _clock, log: _log);
        _config = FpsUserConfig.Load(_configPath);
    }

    /// <summary>Runs the enable/disable poll + capture loop until cancelled. Safe to await
    /// alongside the sampler client task in Program.cs (same shape: run on the thread pool,
    /// cancel on shutdown).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _config = FpsUserConfig.Load(_configPath);
            }

            if (!_config.Enabled)
            {
                lock (_sync)
                {
                    _currentTarget = null;
                }

                await DelayIgnoringCancellation(ConfigPollInterval, cancellationToken);
                continue;
            }

            using var runnerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runnerTask = _runner.RunAsync(runnerCts.Token);

            while (!cancellationToken.IsCancellationRequested && !runnerTask.IsCompleted)
            {
                UpdateCurrentTarget();
                await DelayIgnoringCancellation(ConfigPollInterval, cancellationToken);

                FpsUserConfig reloaded;
                lock (_sync)
                {
                    reloaded = FpsUserConfig.Load(_configPath);
                    _config = reloaded;
                }

                if (!reloaded.Enabled)
                {
                    runnerCts.Cancel();
                    break;
                }
            }

            try
            {
                await runnerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on disable/shutdown.
            }

            lock (_sync)
            {
                _currentTarget = null;
            }
        }
    }

    private static async Task DelayIgnoringCancellation(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Loop condition re-checks cancellationToken next iteration.
        }
    }

    private void UpdateCurrentTarget()
    {
        var latestCompleteBucket = GetGlobalLatestCompleteBucket();
        if (latestCompleteBucket is null)
        {
            lock (_sync)
            {
                _currentTarget = null;
            }

            return;
        }

        var target = _targetSelector.SelectTarget(latestCompleteBucket.Value, _config);
        lock (_sync)
        {
            _currentTarget = target;
        }
    }

    /// <summary>Boot-relative bucket keys aren't wall-clock-comparable across PIDs from
    /// different capture sessions, but within one live PresentMon session every PID shares
    /// the same clock, so "one bucket behind the newest bucket seen anywhere" is a good proxy
    /// for "the most recent bucket every currently-presenting PID has finished".</summary>
    private long? GetGlobalLatestCompleteBucket()
    {
        long? maxKey = null;
        foreach (var pid in _aggregator.GetTrackedPids())
        {
            var keys = _aggregator.GetBucketKeys(pid);
            if (keys.Count == 0)
            {
                continue;
            }

            var last = keys[^1];
            if (maxKey is null || last > maxKey)
            {
                maxKey = last;
            }
        }

        return maxKey is { } k ? k - 1000 : null;
    }

    public SensorFrame Augment(SensorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        FpsTarget? target;
        lock (_sync)
        {
            target = _currentTarget;
        }

        float? avg = null;
        float? low1 = null;
        float? frametimeMs = null;
        float? jitterMs = null;

        if (target is { } t)
        {
            var latestCompleteBucket = GetGlobalLatestCompleteBucket();
            if (latestCompleteBucket is { } bucket)
            {
                var metrics = _aggregator.GetBucketMetrics(t.ProcessId, bucket);
                if (metrics is not null)
                {
                    avg = metrics.AvgFps is { } a && float.IsFinite((float)a) ? (float)a : null;
                    frametimeMs = float.IsFinite((float)metrics.FrametimeMeanMs) ? (float)metrics.FrametimeMeanMs : null;
                    jitterMs = float.IsFinite((float)metrics.FrametimeJitterMs) ? (float)metrics.FrametimeJitterMs : null;
                }

                var pooledLow1 = _aggregator.GetLow1(t.ProcessId, bucket);
                low1 = pooledLow1 is { } l && float.IsFinite((float)l) ? (float)l : null;
            }
        }

        var hardware = new HardwareInfo(FpsSensorIds.HardwareId, FpsSensorIds.HardwareName, FpsSensorIds.HardwareType, null);
        var sensors = new SensorReading[]
        {
            new(FpsSensorIds.Avg, FpsSensorIds.HardwareId, "Average FPS", "Fps", avg, null, null),
            new(FpsSensorIds.Low1, FpsSensorIds.HardwareId, "1% low FPS", "Fps", low1, null, null),
            new(FpsSensorIds.FrametimeMs, FpsSensorIds.HardwareId, "Frame time", "FrameTime", frametimeMs, null, null),
            new(FpsSensorIds.FrametimeJitter, FpsSensorIds.HardwareId, "Frame time jitter", "FrameTime", jitterMs, null, null),
        };

        return frame with
        {
            Hardware = [.. frame.Hardware, hardware],
            Sensors = [.. frame.Sensors, .. sensors],
        };
    }

    public FpsSnapshotInfo Current
    {
        get
        {
            FpsUserConfig config;
            FpsTarget? target;
            lock (_sync)
            {
                config = _config;
                target = _currentTarget;
            }

            if (!config.Enabled)
            {
                return FpsSnapshotInfo.Disabled;
            }

            var status = _runner.Status switch
            {
                FpsRunnerStatus.NotInstalled => FpsStatus.NotInstalled,
                FpsRunnerStatus.IntegrityFailed => FpsStatus.IntegrityFailed,
                FpsRunnerStatus.NeedsPermission => FpsStatus.NeedsPermission,
                FpsRunnerStatus.UnexpectedOutput => FpsStatus.UnexpectedOutput,
                FpsRunnerStatus.Unavailable => FpsStatus.Unavailable,
                FpsRunnerStatus.Starting or FpsRunnerStatus.Disabled => FpsStatus.Starting,
                FpsRunnerStatus.Running => target is null ? FpsStatus.NoTarget : FpsStatus.Tracking,
                _ => FpsStatus.NoTarget,
            };

            return new FpsSnapshotInfo(status, target?.ProcessName, target?.ProcessId);
        }
    }

    /// <summary>Enables/disables FPS capture (docs/SPEC.md step7-fps item 7: the settings
    /// endpoint's <c>fpsEnabled</c> extension). Preserves every other config.json key.</summary>
    public void SetEnabled(bool enabled) =>
        FpsUserConfig.Update(_configPath, fps => fps["enabled"] = enabled);

    /// <summary>Adds a process name to <c>denylistExtra</c> (docs/SPEC.md step7-fps item 7:
    /// the "not tracking?" button's <c>fpsDenylistAdd</c> extension).</summary>
    public void AddToDenylist(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var normalized = FpsTargetSelector.NormalizeProcessName(processName);
        FpsUserConfig.Update(_configPath, fps =>
        {
            var array = fps["denylistExtra"] as System.Text.Json.Nodes.JsonArray ?? [];
            var existing = array.Select(node => node?.GetValue<string>()).Where(v => v is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existing.Add(normalized))
            {
                array.Add(normalized);
            }

            fps["denylistExtra"] = array;
        });
    }

    /// <summary>Sets (or clears, for an empty/null name) the pinned process (docs/SPEC.md
    /// step7-fps item 7: the "pin this app" action).</summary>
    public void SetPinnedProcess(string? processName) =>
        FpsUserConfig.Update(_configPath, fps =>
            fps["pinnedProcess"] = string.IsNullOrWhiteSpace(processName) ? null : processName);
}
