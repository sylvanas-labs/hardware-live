using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

public sealed class FpsTargetSelectorTests
{
    private sealed class FakeForegroundWindowProvider : IForegroundWindowProvider
    {
        public ForegroundWindowSnapshot? Next { get; set; }

        public ForegroundWindowSnapshot? GetForegroundWindow() => Next;
    }

    private static FpsUserConfig Config(string? pinned = null, params string[] denylistExtra) =>
        new(Enabled: true, PinnedProcess: pinned, DenylistExtra: denylistExtra, Invalid: false);

    private static void Present(FpsAggregator aggregator, int pid, string appName, long fromBucketKeyInclusive, int buckets, int framesPerBucket, double fps)
    {
        var frameTime = 1000.0 / fps;
        var now = DateTimeOffset.UnixEpoch;
        for (var b = 0; b < buckets; b++)
        {
            var bucketStart = fromBucketKeyInclusive + (b * 1000L);
            for (var f = 0; f < framesPerBucket; f++)
            {
                var qpc = bucketStart + (f * frameTime);
                aggregator.AddRow(pid, appName, qpc, frameTime, now);
            }
        }
    }

    [Fact]
    public void FullscreenGameAboveThresholdForThreeBucketsIsTarget()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 100, appName: "game.exe", fromBucketKeyInclusive: 0, buckets: 3, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(100, "game.exe", SessionId: 1, CoversWholeMonitor: true),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        var target = selector.SelectTarget(uptoBucketKeyInclusive: 2000, Config());

        Assert.NotNull(target);
        Assert.Equal(100, target!.ProcessId);
        Assert.Equal("game.exe", target.ProcessName);
        Assert.False(target.Pinned);
    }

    [Fact]
    public void WindowedNonFullscreenAppNeverAutoQualifiesEvenAtHighFps()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 200, appName: "somegame.exe", fromBucketKeyInclusive: 0, buckets: 3, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(200, "somegame.exe", SessionId: 1, CoversWholeMonitor: false),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config()));
    }

    [Fact]
    public void DenylistedFullscreenBrowserIsNeverATarget()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 300, appName: "msedge.exe", fromBucketKeyInclusive: 0, buckets: 3, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(300, "msedge.exe", SessionId: 1, CoversWholeMonitor: true),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config()));
    }

    [Fact]
    public void UserConfiguredDenylistExtraIsAlsoRejected()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 301, appName: "launcher.exe", fromBucketKeyInclusive: 0, buckets: 3, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(301, "launcher.exe", SessionId: 1, CoversWholeMonitor: true),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config(pinned: null, "launcher.exe")));
    }

    [Fact]
    public void FewerThanThreeConsecutiveBucketsAboveThresholdIsNoTarget()
    {
        var aggregator = new FpsAggregator();
        // Only 2 buckets of >= 20 fps, not 3.
        Present(aggregator, pid: 400, appName: "game.exe", fromBucketKeyInclusive: 1000, buckets: 2, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(400, "game.exe", SessionId: 1, CoversWholeMonitor: true),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config()));
    }

    [Fact]
    public void SessionZeroProcessIsNeverATarget()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 500, appName: "game.exe", fromBucketKeyInclusive: 0, buckets: 3, framesPerBucket: 60, fps: 60);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(500, "game.exe", SessionId: 0, CoversWholeMonitor: true),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config()));
    }

    [Fact]
    public void NoForegroundWindowIsNoTarget()
    {
        var aggregator = new FpsAggregator();
        var foreground = new FakeForegroundWindowProvider { Next = null };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config()));
    }

    [Fact]
    public void PinnedProcessIsTargetEvenWhenNotForeground()
    {
        var aggregator = new FpsAggregator();
        // Pinned process presenting frames, but a *different* app is in the foreground.
        Present(aggregator, pid: 600, appName: "background-game.exe", fromBucketKeyInclusive: 0, buckets: 1, framesPerBucket: 5, fps: 5);

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(999, "explorer.exe", SessionId: 1, CoversWholeMonitor: false),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        var target = selector.SelectTarget(2000, Config(pinned: "background-game.exe"));

        Assert.NotNull(target);
        Assert.Equal(600, target!.ProcessId);
        Assert.True(target.Pinned);
    }

    [Fact]
    public void PinnedProcessNotCurrentlyPresentingIsNoTarget()
    {
        var aggregator = new FpsAggregator();
        var foreground = new FakeForegroundWindowProvider { Next = null };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(2000, Config(pinned: "not-running.exe")));
    }

    [Fact]
    public void PinnedProcessNameComparisonIsCaseInsensitiveAndIgnoresExeSuffix()
    {
        var aggregator = new FpsAggregator();
        Present(aggregator, pid: 700, appName: "Game.EXE", fromBucketKeyInclusive: 0, buckets: 1, framesPerBucket: 1, fps: 60);

        var foreground = new FakeForegroundWindowProvider { Next = null };
        var selector = new FpsTargetSelector(foreground, aggregator);

        var target = selector.SelectTarget(2000, Config(pinned: "game"));

        Assert.NotNull(target);
        Assert.Equal(700, target!.ProcessId);
    }

    [Fact]
    public void RealFixtureEveryAppYieldsNoTarget()
    {
        // docs/SPEC.md: the real fixture holds windowed Edge/Terminal frames in "Hardware
        // Composed: Independent Flip" -- proof that independent flip alone isn't a game
        // signal. Replaying it with a foreground snapshot that reports "not fullscreen"
        // (the true state of a windowed app) must yield no target regardless of fps.
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "presentmon", "presentmon-2.6.0-v2-raw-stdout.csv");
        var lines = File.ReadAllLines(path);
        Assert.True(HardwareLive.Core.Fps.PresentMonCsvParser.TryParseHeader(lines[0], out var columns));

        var aggregator = new FpsAggregator();
        long maxBucket = long.MinValue;
        var pid = 0;
        var appName = string.Empty;
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i]))
            {
                continue;
            }

            Assert.True(HardwareLive.Core.Fps.PresentMonCsvParser.TryParseRow(lines[i], columns, out var row));
            var now = DateTimeOffset.UnixEpoch.AddMilliseconds(row!.CpuStartQpcTimeMs);
            aggregator.AddRow(row.ProcessId, row.Application, row.CpuStartQpcTimeMs, row.FrameTimeMs, now);
            pid = row.ProcessId;
            appName = row.Application;
            maxBucket = Math.Max(maxBucket, FpsAggregator.BucketKey(row.CpuStartQpcTimeMs));
        }

        var foreground = new FakeForegroundWindowProvider
        {
            Next = new ForegroundWindowSnapshot(pid, appName, SessionId: 1, CoversWholeMonitor: false),
        };
        var selector = new FpsTargetSelector(foreground, aggregator);

        Assert.Null(selector.SelectTarget(maxBucket, Config()));
    }
}
