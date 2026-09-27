using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

/// <summary>
/// Reclassifies only when the latest frame's distinct sensor-id set changes, so a busy
/// <c>/api/meta</c> poller doesn't re-run the classifier every request. Caching is keyed
/// on the sensor-id set, not sensor values, so the classifier still freezes which CCD was
/// hottest at classification time on the AMD control-temp fallback (max of the
/// "CCDn (Tdie)" readings, used only
/// when no "Core (Tctl/Tdie)"-style sensor exists) -- that choice is frozen in
/// <see cref="ClassificationResult.Roles"/> until the sensor-id set changes. This no
/// longer causes stale health analysis: <see cref="ClassificationResult.CpuControlIsCcdMax"/>
/// tells <c>HealthAnalyzer</c> to recompute max(all CCDs) fresh from each sample instead
/// of trusting the cached winner's own history.
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
