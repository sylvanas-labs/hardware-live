using HardwareLive.Protocol;

namespace HardwareLive.Core;

/// <summary>Appends synthetic hardware/sensors to a real sampler frame before it's stored
/// (docs/SPEC.md step7-fps item 5): implemented by <c>Fps.FpsService</c> so the FPS synthetic
/// sensors get history/peaks/staleness/classifier/preset support "for free" through the exact
/// same path every real sensor uses, instead of a parallel telemetry mechanism.</summary>
public interface IFrameAugmentor
{
    SensorFrame Augment(SensorFrame frame);
}

public interface ITelemetrySource
{
    SensorFrame? LatestFrame { get; }

    bool IsStale { get; }

    bool HasSamplerIdentityMismatch { get; }

    TelemetrySnapshot GetSnapshot(IReadOnlyCollection<string>? sensorIds = null);
}

public sealed record SensorPeak(float Value, long TimestampUnixMs);

public sealed record TelemetrySnapshot(
    SensorFrame? LatestFrame,
    bool Stale,
    IReadOnlyDictionary<string, IReadOnlyList<float?>> History);

/// <summary>
/// Bounded, O(1)-per-sensor telemetry store. Each tracked sensor id owns a fixed-size
/// ring buffer instead of the store keeping every raw <see cref="SensorFrame"/>, so
/// <see cref="Add"/> and <see cref="GetSnapshot"/> cost is proportional to the sensors in
/// the incoming frame / the ids requested, never to (tracked ids * frames * sensors).
/// </summary>
public sealed class TelemetryStore : ITelemetrySource
{
    public const int Capacity = 300;
    public const int MaxTrackedSensors = 5000;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, RingBuffer> _histories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SensorPeak> _peaks = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private SensorFrame? _latestFrame;
    private int _windowLength;
    private DateTimeOffset? _lastFrameTime;
    private bool _samplerIdentityMismatch;

    public TelemetryStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>When set, every frame is run through this before being stored (docs/SPEC.md
    /// step7-fps: the FPS synthetic-sensor injection point). Null (the default) is a no-op,
    /// so every existing caller/test keeps working unchanged.</summary>
    public IFrameAugmentor? FrameAugmentor { get; set; }

    public SensorFrame? LatestFrame
    {
        get
        {
            lock (_sync)
            {
                return _latestFrame;
            }
        }
    }

    public bool IsStale
    {
        get
        {
            lock (_sync)
            {
                return IsStaleLocked();
            }
        }
    }

    public bool HasSamplerIdentityMismatch
    {
        get
        {
            lock (_sync)
            {
                return _samplerIdentityMismatch;
            }
        }
    }

    public DateTimeOffset? LastFrameTime
    {
        get
        {
            lock (_sync)
            {
                return _lastFrameTime;
            }
        }
    }

    /// <summary>Number of sensor ids currently tracked with a history ring buffer, always
    /// &lt;= <see cref="MaxTrackedSensors"/>. Exposed for tests.</summary>
    public int TrackedSensorCount
    {
        get
        {
            lock (_sync)
            {
                return _histories.Count;
            }
        }
    }

    public IReadOnlyDictionary<string, SensorPeak> Peaks
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, SensorPeak>(_peaks, StringComparer.Ordinal);
            }
        }
    }

    public void Add(SensorFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (FrameAugmentor is { } augmentor)
        {
            frame = augmentor.Augment(frame);
        }

        lock (_sync)
        {
            _latestFrame = frame;
            _lastFrameTime = _clock.GetUtcNow();
            _samplerIdentityMismatch = false;

            var frameValues = new Dictionary<string, float?>(frame.Sensors.Count, StringComparer.Ordinal);
            var frameIds = new HashSet<string>(frame.Sensors.Count, StringComparer.Ordinal);
            foreach (var sensor in frame.Sensors)
            {
                frameValues[sensor.Id] = sensor.Value;
                frameIds.Add(sensor.Id);

                if (sensor.Value is { } value && float.IsFinite(value))
                {
                    if (!_peaks.TryGetValue(sensor.Id, out var peak) || value > peak.Value)
                    {
                        _peaks[sensor.Id] = new SensorPeak(value, frame.TimestampUnixMs);
                    }
                }
            }

            RegisterNewSensorIds(frameIds);

            foreach (var (id, buffer) in _histories)
            {
                buffer.Push(frameValues.TryGetValue(id, out var value) ? value : null);
            }

            _windowLength = Math.Min(_windowLength + 1, Capacity);

            TrimToCap(_peaks, frameIds);
        }
    }

    public void SetSamplerIdentityMismatch(bool mismatch)
    {
        lock (_sync)
        {
            _samplerIdentityMismatch = mismatch;
        }
    }

    public TelemetrySnapshot GetSnapshot(IReadOnlyCollection<string>? sensorIds = null)
    {
        lock (_sync)
        {
            var ids = sensorIds ?? (IReadOnlyCollection<string>)_histories.Keys.ToArray();
            var history = new Dictionary<string, IReadOnlyList<float?>>(StringComparer.Ordinal);

            foreach (var id in ids)
            {
                history[id] = _histories.TryGetValue(id, out var buffer)
                    ? buffer.ToArray()
                    : new float?[_windowLength];
            }

            return new TelemetrySnapshot(_latestFrame, IsStaleLocked(), history);
        }
    }

    private void RegisterNewSensorIds(HashSet<string> frameIds)
    {
        foreach (var id in frameIds)
        {
            if (_histories.ContainsKey(id))
            {
                continue;
            }

            if (_histories.Count >= MaxTrackedSensors)
            {
                TrimToCap(_histories, frameIds);
            }

            if (_histories.Count >= MaxTrackedSensors)
            {
                // Still at the cap after evicting everything evictable (every tracked id
                // is present in this very frame): drop history tracking for this id. Its
                // value/peak are still current in _latestFrame/_peaks; only its historical
                // series is unavailable.
                continue;
            }

            var buffer = new RingBuffer(Capacity);
            for (var padded = 0; padded < _windowLength; padded++)
            {
                buffer.Push(null);
            }

            _histories[id] = buffer;
        }
    }

    private static void TrimToCap<TValue>(Dictionary<string, TValue> tracked, HashSet<string> latestFrameIds)
    {
        if (tracked.Count < MaxTrackedSensors)
        {
            return;
        }

        var evictable = tracked.Keys.Where(id => !latestFrameIds.Contains(id)).ToArray();
        foreach (var id in evictable)
        {
            if (tracked.Count < MaxTrackedSensors)
            {
                break;
            }

            tracked.Remove(id);
        }
    }

    private bool IsStaleLocked() =>
        _lastFrameTime is null || _clock.GetUtcNow() - _lastFrameTime.Value > StaleAfter;

    /// <summary>
    /// Fixed-capacity FIFO of the most recent N values for one sensor. All tracked
    /// sensors are pushed to on every <see cref="Add"/>, so every buffer always holds
    /// exactly <see cref="TelemetryStore._windowLength"/> entries.
    /// </summary>
    private sealed class RingBuffer(int capacity)
    {
        private readonly float?[] _values = new float?[capacity];
        private int _head;
        private int _count;

        public void Push(float? value)
        {
            if (_count < _values.Length)
            {
                _values[(_head + _count) % _values.Length] = value;
                _count++;
            }
            else
            {
                _values[_head] = value;
                _head = (_head + 1) % _values.Length;
            }
        }

        public float?[] ToArray()
        {
            var result = new float?[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _values[(_head + i) % _values.Length];
            }

            return result;
        }
    }
}
