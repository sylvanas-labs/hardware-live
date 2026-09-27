namespace HardwareLive.Core.Fps;

/// <summary>Observable lifecycle state of <see cref="PresentMonRunner"/> (docs/SPEC.md
/// Component 9 / step7-fps item 1). <see cref="FpsService"/> maps these to the exact
/// user-facing status strings the spec names.</summary>
public enum FpsRunnerStatus
{
    Disabled,
    Starting,
    Running,
    NotInstalled,
    IntegrityFailed,
    NeedsPermission,
    UnexpectedOutput,
    Unavailable,
}

/// <summary>
/// Owns the PresentMon child process lifecycle (docs/SPEC.md Component 9 / step7-fps item 1):
/// exact spawn arguments, a pinned-hash integrity check before ever running the exe, restart
/// with backoff after a crash (2 s / 5 s / 10 s, "FPS unavailable" after 3 consecutive failures
/// within 5 minutes), and an immediate, non-retrying "needs-permission" status on an
/// access-denied exit. Parses each stdout line via <see cref="PresentMonCsvParser"/> and feeds
/// valid rows into a <see cref="FpsAggregator"/>. The process launcher and clock/delay are
/// injectable so this is unit testable without spawning a real PresentMon.exe.
/// </summary>
public sealed class PresentMonRunner
{
    /// <summary>Exact spawn arguments (docs/SPEC.md Component 9, verified against the real
    /// binary) -- never changed without re-verifying against the real exe.</summary>
    public const string Arguments =
        "--output_stdout --no_csv --no_console_stats --v2_metrics --qpc_time_ms --session_name HardwareLive --stop_existing_session";

    /// <summary>Untrusted process stdout is bounded to this many parsed rows per second;
    /// excess rows on a runaway stream are dropped rather than queued (docs/SPEC.md
    /// step7-fps: "bounded rows/s - drop excess").</summary>
    public const int MaxRowsPerSecond = 2000;

    private static readonly TimeSpan[] BackoffSchedule =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private readonly string _exePath;
    private readonly FpsAggregator _aggregator;
    private readonly IPresentMonProcessLauncher _launcher;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string> _log;

    private readonly Func<string, bool> _verifyIntegrity;

    public PresentMonRunner(
        string exePath,
        FpsAggregator aggregator,
        IPresentMonProcessLauncher? launcher = null,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null,
        Func<string, bool>? verifyIntegrity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        _exePath = exePath;
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _launcher = launcher ?? new Win32PresentMonProcessLauncher();
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _log = log ?? (_ => { });
        _verifyIntegrity = verifyIntegrity ?? PresentMonHash.Verify;
    }

    public FpsRunnerStatus Status { get; private set; } = FpsRunnerStatus.Disabled;

    /// <summary>Number of times <see cref="_launcher"/> was asked to start a process. Exposed
    /// for tests asserting the restart/backoff count.</summary>
    public int LaunchAttempts { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_exePath))
        {
            Status = FpsRunnerStatus.NotInstalled;
            return;
        }

        if (!_verifyIntegrity(_exePath))
        {
            Status = FpsRunnerStatus.IntegrityFailed;
            _log("PresentMon integrity check failed: refusing to run.");
            return;
        }

        var failureTimestamps = new Queue<DateTimeOffset>();
        while (!cancellationToken.IsCancellationRequested)
        {
            Status = FpsRunnerStatus.Starting;
            var accessDenied = false;
            var unexpectedOutput = false;
            IManagedProcess? process = null;
            try
            {
                LaunchAttempts++;
                process = _launcher.Start(_exePath, Arguments);
                Status = FpsRunnerStatus.Running;
                unexpectedOutput = !await ReadLoopAsync(process, cancellationToken);
                accessDenied = !unexpectedOutput && LooksLikeAccessDenied(process);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"PresentMon runner failed: {exception.Message}");
            }
            finally
            {
                if (process is not null)
                {
                    await process.DisposeAsync();
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (unexpectedOutput)
            {
                Status = FpsRunnerStatus.UnexpectedOutput;
                return;
            }

            if (accessDenied)
            {
                Status = FpsRunnerStatus.NeedsPermission;
                return;
            }

            var now = _clock.GetUtcNow();
            failureTimestamps.Enqueue(now);
            while (failureTimestamps.Count > 0 && now - failureTimestamps.Peek() > FailureWindow)
            {
                failureTimestamps.Dequeue();
            }

            if (failureTimestamps.Count >= MaxConsecutiveFailures)
            {
                Status = FpsRunnerStatus.Unavailable;
                _log("PresentMon failed 3 times within 5 minutes; FPS unavailable.");
                return;
            }

            var backoffIndex = Math.Min(failureTimestamps.Count - 1, BackoffSchedule.Length - 1);
            try
            {
                await _delay(BackoffSchedule[backoffIndex], cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Reads the header then data lines until the process's stdout ends. Returns
    /// false when the header fails validation (docs/SPEC.md: "reject a header lacking
    /// FrameTime/CPUStartQPCTime/ProcessID"), true otherwise (including a clean EOF with no
    /// header at all, treated as an ordinary process exit, not a format problem).</summary>
    private async Task<bool> ReadLoopAsync(IManagedProcess process, CancellationToken cancellationToken)
    {
        var headerLine = await process.ReadOutputLineAsync(cancellationToken);
        if (headerLine is null)
        {
            await process.WaitForExitAsync(cancellationToken);
            return true;
        }

        if (!PresentMonCsvParser.TryParseHeader(headerLine, out var columns))
        {
            process.Kill();
            await process.WaitForExitAsync(cancellationToken);
            return false;
        }

        string? line;
        var rowsThisWindow = 0;
        var windowStart = _clock.GetUtcNow();
        while ((line = await process.ReadOutputLineAsync(cancellationToken)) is not null)
        {
            var now = _clock.GetUtcNow();
            if (now - windowStart >= TimeSpan.FromSeconds(1))
            {
                windowStart = now;
                rowsThisWindow = 0;
            }

            rowsThisWindow++;
            if (rowsThisWindow > MaxRowsPerSecond)
            {
                continue;
            }

            if (!PresentMonCsvParser.TryParseRow(line, columns, out var row) || row is null)
            {
                continue;
            }

            _aggregator.AddRow(row.ProcessId, row.Application, row.CpuStartQpcTimeMs, row.FrameTimeMs, now);
        }

        await process.WaitForExitAsync(cancellationToken);
        return true;
    }

    private static bool LooksLikeAccessDenied(IManagedProcess process)
    {
        var tail = process.StandardErrorTail;
        return tail.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase);
    }
}
