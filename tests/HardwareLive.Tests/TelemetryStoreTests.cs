using HardwareLive.Core;
using HardwareLive.Protocol;

namespace HardwareLive.Tests;

public sealed class TelemetryStoreTests
{
    [Fact]
    public void RingBufferKeepsTheLastThreeHundredFrames()
    {
        var store = new TelemetryStore();
        for (var index = 0; index < 305; index++)
        {
            store.Add(Frame(index, index));
        }

        var snapshot = store.GetSnapshot();
        var values = snapshot.History["/cpu/0/temp/0"];

        Assert.Equal(TelemetryStore.Capacity, values.Count);
        Assert.Equal(5f, values[0]);
        Assert.Equal(304f, values[^1]);
        Assert.Equal(304, snapshot.LatestFrame!.Sequence);
    }

    [Fact]
    public void HistoryPreservesNullsAndAlignsMissingSensors()
    {
        var store = new TelemetryStore();
        store.Add(Frame(1, null));
        store.Add(Frame(2, 42));
        store.Add(Frame(3, 43) with { Sensors = [] });

        var values = store.GetSnapshot(["/cpu/0/temp/0"]).History["/cpu/0/temp/0"];

        Assert.Equal(new float?[] { null, 42, null }, values);
    }

    [Fact]
    public void UnknownIdReturnsNullPaddedHistoryMatchingTheWindowLength()
    {
        var store = new TelemetryStore();
        store.Add(Frame(1, 40));
        store.Add(Frame(2, 41));

        var values = store.GetSnapshot(["never-seen"]).History["never-seen"];

        Assert.Equal(new float?[] { null, null }, values);
    }

    [Fact]
    public void TrackedSensorCountIsCappedAndEvictsIdsMissingFromTheLatestFrame()
    {
        var store = new TelemetryStore();

        // Fill exactly to the cap with distinct sensor ids, all present in one frame.
        var fullSensors = Enumerable.Range(0, TelemetryStore.MaxTrackedSensors)
            .Select(index => new SensorReading($"/s/{index}", "/cpu/0", "Name", "Temperature", index, null, null))
            .ToArray();
        store.Add(new SensorFrame(
            SensorFrame.CurrentVersion, 1, 1, false, false, "0.9.6.0",
            [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)], fullSensors));

        Assert.Equal(TelemetryStore.MaxTrackedSensors, store.TrackedSensorCount);

        // A frame with one brand-new id and none of the old ones: at least one old id
        // must be evicted to make room, and the count must never exceed the cap.
        store.Add(new SensorFrame(
            SensorFrame.CurrentVersion, 2, 2, false, false, "0.9.6.0",
            [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            [new SensorReading("/s/new", "/cpu/0", "Name", "Temperature", 99, null, null)]));

        Assert.True(store.TrackedSensorCount <= TelemetryStore.MaxTrackedSensors);
        var newIdHistory = store.GetSnapshot(["/s/new"]).History["/s/new"];
        Assert.Equal(99f, newIdHistory[^1]);

        var evicted = Enumerable.Range(0, TelemetryStore.MaxTrackedSensors)
            .Select(index => $"/s/{index}")
            .First(id => store.GetSnapshot([id]).History[id].All(value => value is null));
        Assert.NotNull(evicted);
    }

    [Fact]
    public void SessionPeakKeepsValueAndFrameTimestamp()
    {
        var store = new TelemetryStore();
        store.Add(Frame(10, 80));
        store.Add(Frame(20, 70));
        store.Add(Frame(30, 90));

        Assert.Equal(new SensorPeak(90, 30), store.Peaks["/cpu/0/temp/0"]);
    }

    [Fact]
    public void StaleUsesInjectedArrivalClock()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var store = new TelemetryStore(clock);

        Assert.True(store.IsStale);
        store.Add(Frame(1, 42));
        Assert.False(store.IsStale);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(store.IsStale);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(store.IsStale);
    }

    private static SensorFrame Frame(long sequence, float? value) =>
        new(
            SensorFrame.CurrentVersion,
            sequence,
            sequence,
            false,
            false,
            "0.9.6.0",
            [new HardwareInfo("/cpu/0", "CPU", "Cpu", null)],
            [new SensorReading("/cpu/0/temp/0", "/cpu/0", "Package", "Temperature", value, null, null)]);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
