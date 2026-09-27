namespace HardwareLive.Core;

internal sealed class InMemoryLayoutStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Layout> _layouts = new(StringComparer.Ordinal);

    public bool Create(Layout layout)
    {
        lock (_lock)
        {
            return _layouts.TryAdd(layout.Id, layout);
        }
    }

    public void Replace(Layout layout)
    {
        lock (_lock)
        {
            _layouts[layout.Id] = layout;
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _layouts.Remove(id);
        }
    }

    public void Import(IEnumerable<Layout> layouts)
    {
        lock (_lock)
        {
            foreach (var layout in layouts)
            {
                _layouts[layout.Id] = layout;
            }
        }
    }

    public IReadOnlyList<Layout> List()
    {
        lock (_lock)
        {
            return _layouts.Values.OrderBy(layout => layout.Id, StringComparer.Ordinal).ToArray();
        }
    }
}
