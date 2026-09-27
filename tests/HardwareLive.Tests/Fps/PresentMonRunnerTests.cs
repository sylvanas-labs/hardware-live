using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

public sealed class PresentMonRunnerTests
{
    private sealed class FakeManagedProcess : IManagedProcess
    {
        private readonly Queue<string?> _outputLines;

        public FakeManagedProcess(IEnumerable<string?> outputLines) => _outputLines = new Queue<string?>(outputLines);

        public int Id { get; init; } = 4242;

        public string StandardErrorTail { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public bool Disposed { get; private set; }

        public Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_outputLines.Count > 0 ? _outputLines.Dequeue() : null);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(ExitCode);

        public void Kill()
        {
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IPresentMonProcessLauncher
    {
        public List<string> StartedArguments { get; } = [];

        public Func<FakeManagedProcess> Factory { get; set; } = () => new FakeManagedProcess([null]);

        public IManagedProcess Start(string exePath, string arguments)
        {
            StartedArguments.Add(arguments);
            return Factory();
        }
    }

    private static string RealHeader() =>
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing," +
        "PresentMode,CPUStartQPCTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait," +
        "DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency," +
        "ClickToPhotonLatency";

    [Fact]
    public async Task MissingExeYieldsNotInstalledWithoutLaunching()
    {
        var launcher = new FakeLauncher();
        var runner = new PresentMonRunner(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.exe"),
            new FpsAggregator(),
            launcher);

        await runner.RunAsync(CancellationToken.None);

        Assert.Equal(FpsRunnerStatus.NotInstalled, runner.Status);
        Assert.Empty(launcher.StartedArguments);
    }

    [Fact]
    public async Task IntegrityMismatchRefusesToRunWithoutLaunching()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"fake-presentmon-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(tempExe, "not the real binary");
        try
        {
            var launcher = new FakeLauncher();
            var runner = new PresentMonRunner(
                tempExe,
                new FpsAggregator(),
                launcher,
                verifyIntegrity: _ => false);

            await runner.RunAsync(CancellationToken.None);

            Assert.Equal(FpsRunnerStatus.IntegrityFailed, runner.Status);
            Assert.Empty(launcher.StartedArguments);
        }
        finally
        {
            File.Delete(tempExe);
        }
    }

    [Fact]
    public async Task AccessDeniedExitGoesStraightToNeedsPermissionWithoutRestarting()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"fake-presentmon-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(tempExe, "placeholder");
        try
        {
            var launcher = new FakeLauncher
            {
                Factory = () => new FakeManagedProcess([null])
                {
                    ExitCode = 5,
                    StandardErrorTail = "presentmon: access is denied (not a member of Performance Log Users)",
                },
            };

            var runner = new PresentMonRunner(
                tempExe,
                new FpsAggregator(),
                launcher,
                delay: (_, _) => throw new InvalidOperationException("Should never back off/restart on access-denied."),
                verifyIntegrity: _ => true);

            await runner.RunAsync(CancellationToken.None);

            Assert.Equal(FpsRunnerStatus.NeedsPermission, runner.Status);
            Assert.Single(launcher.StartedArguments);
        }
        finally
        {
            File.Delete(tempExe);
        }
    }

    [Fact]
    public async Task ThreeConsecutiveCrashesWithinTheWindowGoUnavailableAfterBackoffRestarts()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"fake-presentmon-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(tempExe, "placeholder");
        try
        {
            var launcher = new FakeLauncher
            {
                // Every launch immediately returns EOF (header line null) with a clean exit:
                // an ordinary crash/exit, not access-denied.
                Factory = () => new FakeManagedProcess([null]) { ExitCode = 1 },
            };

            var delaysRequested = new List<TimeSpan>();
            var runner = new PresentMonRunner(
                tempExe,
                new FpsAggregator(),
                launcher,
                delay: (span, _) =>
                {
                    delaysRequested.Add(span);
                    return Task.CompletedTask;
                },
                verifyIntegrity: _ => true);

            await runner.RunAsync(CancellationToken.None);

            Assert.Equal(FpsRunnerStatus.Unavailable, runner.Status);
            Assert.Equal(3, launcher.StartedArguments.Count);
            // Backoff schedule is 2s then 5s between the 3 attempts (docs/SPEC.md step7-fps).
            Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], delaysRequested);
        }
        finally
        {
            File.Delete(tempExe);
        }
    }

    [Fact]
    public async Task UnexpectedHeaderYieldsUnexpectedOutputStatus()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"fake-presentmon-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(tempExe, "placeholder");
        try
        {
            var launcher = new FakeLauncher
            {
                Factory = () => new FakeManagedProcess(["Application,SomeOtherColumn", null]),
            };

            var runner = new PresentMonRunner(
                tempExe,
                new FpsAggregator(),
                launcher,
                delay: (_, _) => throw new InvalidOperationException("Should never back off/restart on bad header."),
                verifyIntegrity: _ => true);

            await runner.RunAsync(CancellationToken.None);

            Assert.Equal(FpsRunnerStatus.UnexpectedOutput, runner.Status);
        }
        finally
        {
            File.Delete(tempExe);
        }
    }

    [Fact]
    public async Task ValidRowsAreFedIntoTheAggregator()
    {
        var tempExe = Path.Combine(Path.GetTempPath(), $"fake-presentmon-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(tempExe, "placeholder");
        try
        {
            const string row =
                "game.exe,777,0x0,DXGI,0,0,0,Hardware: Legacy Flip,1000.0,16.6,1,1,1,1,1,1,1,NA,NA,1,NA,NA,NA";
            var launcher = new FakeLauncher
            {
                Factory = () => new FakeManagedProcess([RealHeader(), row, null]),
            };

            var aggregator = new FpsAggregator();
            var runner = new PresentMonRunner(
                tempExe,
                aggregator,
                launcher,
                delay: (_, _) => throw new InvalidOperationException("Should not restart after a clean single-shot fake process."),
                verifyIntegrity: _ => true);

            // A single clean read-to-EOF is not access-denied and not a header failure, so it
            // falls through to the restart/backoff path -- run with a short timeout instead of
            // awaiting RunAsync to completion (it would otherwise try to back off and restart
            // forever using the throwing delay above once the first failure is registered).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await runner.RunAsync(cts.Token);
            }
            catch (InvalidOperationException)
            {
                // Expected once the loop tries to back off after the single fake run ends.
            }

            Assert.Equal(777, Assert.Single(aggregator.GetTrackedPids()));
            var bucket = aggregator.GetBucketMetrics(777, FpsAggregator.BucketKey(1000.0));
            Assert.NotNull(bucket);
            Assert.Equal(1, bucket!.FrameCount);
        }
        finally
        {
            File.Delete(tempExe);
        }
    }
}
