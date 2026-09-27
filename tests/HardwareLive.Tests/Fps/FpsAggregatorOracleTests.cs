using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

/// <summary>
/// docs/SPEC.md step7-fps acceptance criteria "Deterministic oracle": replays the synthetic
/// 60 s fixture through the real <see cref="PresentMonCsvParser"/>/<see cref="FpsAggregator"/>
/// and compares every bucket against <c>tests/oracle/fps_oracle.py</c>'s independently
/// computed, committed expected output -- within 0.01 for every metric, per the spec.
/// </summary>
public sealed class FpsAggregatorOracleTests
{
    private sealed record ExpectedBucket(
        long BucketKey, int N, double? AvgFps, double FrametimeMs, double JitterMs, double? Low1Fps);

    private sealed record ExpectedOutput(int Pid, IReadOnlyList<ExpectedBucket> Buckets);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void EveryBucketMatchesTheIndependentPythonOracleWithinTolerance()
    {
        var expected = LoadExpected();
        Assert.True(expected.Buckets.Count > 50, "Expected fixture should span roughly 60 one-second buckets.");

        var aggregator = ReplayFixtureIntoAggregator();

        foreach (var bucket in expected.Buckets)
        {
            var actual = aggregator.GetBucketMetrics(expected.Pid, bucket.BucketKey);
            Assert.NotNull(actual);
            Assert.Equal(bucket.N, actual!.FrameCount);

            AssertCloseOrBothNull(bucket.AvgFps, actual.AvgFps, $"avgFps @ {bucket.BucketKey}");
            Assert.Equal(bucket.FrametimeMs, actual.FrametimeMeanMs, precision: 2);
            Assert.Equal(bucket.JitterMs, actual.FrametimeJitterMs, precision: 2);

            var actualLow1 = aggregator.GetLow1(expected.Pid, bucket.BucketKey);
            AssertCloseOrBothNull(bucket.Low1Fps, actualLow1, $"low1Fps @ {bucket.BucketKey}");
        }
    }

    private static void AssertCloseOrBothNull(double? expected, double? actual, string context)
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"{context}: expected {expected?.ToString() ?? "null"}, actual {actual?.ToString() ?? "null"}");
            return;
        }

        Assert.True(
            Math.Abs(expected.Value - actual.Value) <= 0.01,
            $"{context}: expected {expected.Value}, actual {actual.Value}");
    }

    private static FpsAggregator ReplayFixtureIntoAggregator()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "presentmon", "synthetic-game-60s.csv");
        var lines = File.ReadAllLines(path);
        Assert.True(PresentMonCsvParser.TryParseHeader(lines[0], out var columns));

        var aggregator = new FpsAggregator();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i]))
            {
                continue;
            }

            Assert.True(PresentMonCsvParser.TryParseRow(lines[i], columns, out var row));
            var nowUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(row!.CpuStartQpcTimeMs);
            aggregator.AddRow(row.ProcessId, row.Application, row.CpuStartQpcTimeMs, row.FrameTimeMs, nowUtc);
        }

        return aggregator;
    }

    private static ExpectedOutput LoadExpected()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "oracle", "synthetic-game-60s.expected.json");
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<ExpectedOutput>(json, JsonOptions);
        Assert.NotNull(document);
        return document!;
    }
}
