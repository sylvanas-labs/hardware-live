namespace HardwareLive.Core.Fps;

/// <summary>Per-bucket derived values for one PID (docs/SPEC.md Component 9, exact formulas).
/// <see cref="AvgFps"/> is null when the bucket has fewer than 2 frames.</summary>
public sealed record FpsBucketMetrics(double? AvgFps, double FrametimeMeanMs, double FrametimeJitterMs, int FrameCount);

/// <summary>
/// Bounded, per-PID rolling aggregator over PresentMon rows (docs/SPEC.md Component 9 /
/// step7-fps item 3). Frames are bucketed by <c>CPUStartQPCTime</c> into aligned, half-open
/// 1 s buckets; every row PresentMon emits for a PID counts once its FrameTime is not NA/&lt;=0.
/// Memory is bounded per PID (<see cref="MaxBucketsPerPid"/>, oldest evicted first) and across
/// PIDs (<see cref="MaxTrackedPids"/>, idle PIDs evicted after <see cref="IdleEvictAfter"/>).
/// This class has no notion of wall-clock "now" for eviction on its own -- the caller passes
/// it in, so both live use (PresentMonRunner's real clock) and the deterministic oracle test
/// (a fixed replay clock) share the exact same code path.
/// </summary>
public sealed class FpsAggregator
{
    public const int MaxBucketsPerPid = 120;
    public const int MaxTrackedPids = 64;
    public static readonly TimeSpan IdleEvictAfter = TimeSpan.FromSeconds(10);
    private const int Low1WindowBuckets = 60;
    private const int Low1MinFrames = 100;
    private const double Low1Percentile = 0.99;

    private readonly Dictionary<int, PidState> _pids = new();

    private sealed class PidState
    {
        // Ascending by bucket key (bucket start, ms). SortedDictionary keeps eviction of the
        // oldest bucket (and enumeration for the low1 pool) O(log n) instead of a full sort
        // on every row.
        public readonly SortedDictionary<long, List<double>> Buckets = new();
        public DateTimeOffset LastSeenUtc;
        public string ApplicationName = string.Empty;
    }

    /// <summary>Adds one row for <paramref name="pid"/>. Rows with a null or non-positive
    /// FrameTime are skipped entirely per the spec (they still count as "seen" for eviction
    /// purposes via <paramref name="nowUtc"/>, since a PID can legitimately present a
    /// dropped/NA frame while still very much running). <paramref name="applicationName"/> is
    /// remembered even for a skipped row -- the target selector needs to find a pinned
    /// process by name even while it's momentarily emitting only NA frames.</summary>
    public void AddRow(int pid, string applicationName, double cpuStartQpcTimeMs, double? frameTimeMs, DateTimeOffset nowUtc)
    {
        EvictIdlePids(nowUtc);

        if (!_pids.TryGetValue(pid, out var state))
        {
            if (_pids.Count >= MaxTrackedPids)
            {
                EvictOldestPid();
            }

            if (_pids.Count >= MaxTrackedPids)
            {
                // Every tracked PID was touched inside the idle window: nothing evictable.
                // Drop this row's aggregation rather than grow unbounded.
                return;
            }

            state = new PidState();
            _pids[pid] = state;
        }

        state.LastSeenUtc = nowUtc;
        if (!string.IsNullOrEmpty(applicationName))
        {
            state.ApplicationName = applicationName;
        }

        if (frameTimeMs is not { } frameTime || frameTime <= 0 || !double.IsFinite(frameTime))
        {
            return;
        }

        var bucketKey = BucketKey(cpuStartQpcTimeMs);
        if (!state.Buckets.TryGetValue(bucketKey, out var list))
        {
            list = [];
            state.Buckets[bucketKey] = list;

            while (state.Buckets.Count > MaxBucketsPerPid)
            {
                var oldestKey = state.Buckets.Keys.First();
                state.Buckets.Remove(oldestKey);
            }
        }

        list.Add(frameTime);
    }

    /// <summary>Aligned half-open 1 s bucket start, in the same ms units as
    /// <c>--qpc_time_ms</c>'s CPUStartQPCTime (docs/SPEC.md: "aligned, half-open 1 s buckets
    /// [k, k+1000) ms"). Boot-relative, not epoch-aligned -- callers never compare bucket
    /// keys across PIDs from different capture sessions.</summary>
    public static long BucketKey(double cpuStartQpcTimeMs) => (long)Math.Floor(cpuStartQpcTimeMs / 1000.0) * 1000;

    /// <summary>Every bucket key currently tracked for a PID, ascending. Empty for an
    /// untracked PID.</summary>
    public IReadOnlyList<long> GetBucketKeys(int pid) =>
        _pids.TryGetValue(pid, out var state) ? state.Buckets.Keys.ToArray() : [];

    /// <summary>fps.avg[k], frametime.ms[k], frametime.jitter[k] for one bucket
    /// (docs/SPEC.md exact formulas). Null when the bucket has no tracked frames at all.</summary>
    public FpsBucketMetrics? GetBucketMetrics(int pid, long bucketKey)
    {
        if (!_pids.TryGetValue(pid, out var state) || !state.Buckets.TryGetValue(bucketKey, out var frames) ||
            frames.Count == 0)
        {
            return null;
        }

        var n = frames.Count;
        var sum = 0.0;
        foreach (var ft in frames)
        {
            sum += ft;
        }

        var mean = sum / n;
        var variance = 0.0;
        foreach (var ft in frames)
        {
            var delta = ft - mean;
            variance += delta * delta;
        }

        variance /= n; // population stddev, per spec.
        var jitter = Math.Sqrt(variance);
        double? avgFps = n >= 2 ? 1000.0 * n / sum : null;

        return new FpsBucketMetrics(avgFps, mean, jitter, n);
    }

    /// <summary>
    /// fps.low1 as of <paramref name="uptoBucketKeyInclusive"/> (docs/SPEC.md): pools every
    /// frame from the most recent <see cref="Low1WindowBuckets"/> tracked bucket keys at or
    /// before that key, then takes 1000 / nearest-rank P99 of the pooled frame times. Null
    /// when the pool has fewer than <see cref="Low1MinFrames"/> frames.
    /// </summary>
    public double? GetLow1(int pid, long uptoBucketKeyInclusive)
    {
        if (!_pids.TryGetValue(pid, out var state))
        {
            return null;
        }

        var pooled = new List<double>();
        var windowCount = 0;
        // SortedDictionary.Keys enumerates ascending; walk it in reverse to take the most
        // recent <= uptoBucketKeyInclusive buckets without a full-list sort per call.
        foreach (var key in state.Buckets.Keys.Reverse())
        {
            if (key > uptoBucketKeyInclusive)
            {
                continue;
            }

            pooled.AddRange(state.Buckets[key]);
            windowCount++;
            if (windowCount >= Low1WindowBuckets)
            {
                break;
            }
        }

        if (pooled.Count < Low1MinFrames)
        {
            return null;
        }

        pooled.Sort();
        var rankIndex = (int)Math.Ceiling(Low1Percentile * pooled.Count) - 1;
        rankIndex = Math.Clamp(rankIndex, 0, pooled.Count - 1);
        var p99 = pooled[rankIndex];
        return p99 > 0 ? 1000.0 / p99 : null;
    }

    /// <summary>Frame count in one bucket (0 for an untracked bucket/PID) -- the target
    /// selector's "presented at &gt;= 20 fps for 3 consecutive buckets" check is exactly this
    /// count, since each bucket already spans 1 s.</summary>
    public int GetFrameCount(int pid, long bucketKey) =>
        _pids.TryGetValue(pid, out var state) && state.Buckets.TryGetValue(bucketKey, out var frames)
            ? frames.Count
            : 0;

    /// <summary>Every PID currently tracked (has been seen within <see cref="IdleEvictAfter"/>
    /// of the last <see cref="AddRow"/> call), in no particular order.</summary>
    public IReadOnlyList<int> GetTrackedPids() => _pids.Keys.ToArray();

    /// <summary>The most recent non-empty Application name PresentMon reported for
    /// <paramref name="pid"/>, or empty for an untracked PID.</summary>
    public string GetApplicationName(int pid) =>
        _pids.TryGetValue(pid, out var state) ? state.ApplicationName : string.Empty;

    private void EvictIdlePids(DateTimeOffset nowUtc)
    {
        List<int>? toRemove = null;
        foreach (var (pid, state) in _pids)
        {
            if (nowUtc - state.LastSeenUtc > IdleEvictAfter)
            {
                (toRemove ??= []).Add(pid);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (var pid in toRemove)
        {
            _pids.Remove(pid);
        }
    }

    private void EvictOldestPid()
    {
        var oldest = _pids.OrderBy(kv => kv.Value.LastSeenUtc).FirstOrDefault();
        if (oldest.Value is not null)
        {
            _pids.Remove(oldest.Key);
        }
    }
}
