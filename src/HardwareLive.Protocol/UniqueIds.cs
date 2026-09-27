namespace HardwareLive.Protocol;

/// <summary>
/// LHM does not guarantee unique identifiers: on an RTX 5090 (LHM 0.9.6) "GPU Bus" and
/// "GPU Memory" load both report <c>/gpu-nvidia/0/load/3</c>. Everything downstream keys
/// history by id, so the sampler makes ids unique before sending a frame: the first
/// occurrence keeps its id, later ones get <c>~2</c>, <c>~3</c>, ... LHM enumerates sensors
/// in a stable order, so the suffixed ids stay stable across frames.
/// </summary>
public sealed class UniqueIds
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    public string MakeUnique(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!_seen.TryGetValue(id, out var count))
        {
            _seen[id] = 1;
            return id;
        }

        while (true)
        {
            count++;
            var candidate = $"{id}~{count}";
            _seen[id] = count;
            if (_seen.TryAdd(candidate, 1))
            {
                return candidate;
            }
        }
    }
}
