using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

/// <summary>
/// Reclassifies only when the latest frame's distinct sensor-id set changes, so a busy
/// <c>/api/meta</c> poller doesn't re-run the classifier every request. Known tradeoff:
/// the AMD control-temp fallback (max of the "CCDn (Tdie)" readings, used only when no
/// "Core (Tctl/Tdie)"-style sensor exists) depends on live values, so on that fallback
/// path the cached result can lag by up to one id-set-unchanged window. Accepted per
/// spec: caching is keyed on the sensor-id set, not sensor values.
/// </summary>
public sealed class ClassificationCache
{
    private readonly object _sync = new();
    private string[]? _lastSensorIds;
    private ClassificationResult _lastResult = ClassificationResult.Empty;

    public ClassificationResult Classify(SensorFrame? frame)
    {
        if (frame is null)
        {
            lock (_sync)
            {
                _lastSensorIds = null;
                _lastResult = ClassificationResult.Empty;
                return _lastResult;
            }
        }

        var ids = frame.Sensors
            .Select(s => s.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        lock (_sync)
        {
            if (_lastSensorIds is not null && _lastSensorIds.AsSpan().SequenceEqual(ids))
            {
                return _lastResult;
            }

            _lastResult = SensorClassifier.Classify(frame);
            _lastSensorIds = ids;
            return _lastResult;
        }
    }
}
