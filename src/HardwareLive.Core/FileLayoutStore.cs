using System.Text.Json;
using System.Text.Json.Serialization;

namespace HardwareLive.Core;

internal enum LayoutWriteResult
{
    Success,
    Conflict,
    LimitExceeded,
    ReservedId,
    UnknownPreset,
}

internal sealed record LayoutListItem(Layout Layout, bool Builtin);

internal sealed record LayoutRename(string From, string To);

internal sealed record LayoutImportResult(
    bool Success,
    LayoutWriteResult Result,
    int Imported,
    IReadOnlyList<LayoutRename> Renamed);

internal sealed record LayoutSettings(string? ActivePresetId, string? TemperatureUnit);

internal sealed class FileLayoutStore
{
    public const int MaximumFileSize = 2 * 1024 * 1024;
    public const int MaximumCustomLayouts = 200;

    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions FileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    private readonly TimeProvider _clock;
    private readonly Action? _beforeCommit;
    private IReadOnlyList<Layout> _layouts = [];
    private LayoutSettings _settings = new(null, null);

    public FileLayoutStore(string directory, TimeProvider? clock = null, Action? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "layouts.json");
        _tempPath = Path.Combine(directory, "layouts.json.tmp");
        _backupPath = Path.Combine(directory, "layouts.json.bak");
        _clock = clock ?? TimeProvider.System;
        _beforeCommit = beforeCommit;
        Load();
    }

    public bool SavedLayoutsCouldNotBeRead { get; private set; }

    public IReadOnlyList<LayoutListItem> List()
    {
        lock (_lock)
        {
            return BuiltInLayouts.All
                .Select(layout => new LayoutListItem(layout, true))
                .Concat(_layouts.OrderBy(layout => layout.Id, StringComparer.Ordinal).Select(layout => new LayoutListItem(layout, false)))
                .ToArray();
        }
    }

    public LayoutSettings GetSettings()
    {
        lock (_lock)
        {
            return _settings;
        }
    }

    public LayoutWriteResult Create(Layout layout)
    {
        lock (_lock)
        {
            if (LayoutJson.IsReservedLayoutId(layout.Id))
            {
                return LayoutWriteResult.ReservedId;
            }

            if (_layouts.Any(existing => existing.Id == layout.Id))
            {
                return LayoutWriteResult.Conflict;
            }

            if (_layouts.Count >= MaximumCustomLayouts)
            {
                return LayoutWriteResult.LimitExceeded;
            }

            var next = _layouts.Append(layout).ToArray();
            Persist(next, _settings);
            _layouts = next;
            return LayoutWriteResult.Success;
        }
    }

    public LayoutWriteResult Replace(Layout layout)
    {
        lock (_lock)
        {
            if (LayoutJson.IsReservedLayoutId(layout.Id))
            {
                return LayoutWriteResult.ReservedId;
            }

            var index = _layouts.ToList().FindIndex(existing => existing.Id == layout.Id);
            if (index < 0 && _layouts.Count >= MaximumCustomLayouts)
            {
                return LayoutWriteResult.LimitExceeded;
            }

            var next = _layouts.ToList();
            if (index >= 0)
            {
                next[index] = layout;
            }
            else
            {
                next.Add(layout);
            }

            Persist(next, _settings);
            _layouts = next.ToArray();
            return LayoutWriteResult.Success;
        }
    }

    public LayoutWriteResult Delete(string id)
    {
        lock (_lock)
        {
            if (LayoutJson.IsReservedLayoutId(id))
            {
                return LayoutWriteResult.ReservedId;
            }

            var next = _layouts.Where(layout => layout.Id != id).ToArray();
            if (next.Length == _layouts.Count)
            {
                return LayoutWriteResult.Success;
            }

            var settings = _settings.ActivePresetId == id ? _settings with { ActivePresetId = null } : _settings;
            Persist(next, settings);
            _layouts = next;
            _settings = settings;
            return LayoutWriteResult.Success;
        }
    }

    public LayoutImportResult Import(IEnumerable<Layout> layouts)
    {
        lock (_lock)
        {
            var incoming = layouts.ToArray();
            if (incoming.Any(layout => LayoutJson.IsReservedLayoutId(layout.Id)))
            {
                return new(false, LayoutWriteResult.ReservedId, 0, []);
            }

            if (_layouts.Count + incoming.Length > MaximumCustomLayouts)
            {
                return new(false, LayoutWriteResult.LimitExceeded, 0, []);
            }

            var used = _layouts.Select(layout => layout.Id).ToHashSet(StringComparer.Ordinal);
            var renamed = new List<LayoutRename>();
            var imported = new List<Layout>(incoming.Length);
            foreach (var layout in incoming)
            {
                var id = layout.Id;
                if (used.Contains(id))
                {
                    var suffix = 2;
                    do
                    {
                        var suffixText = $"-{suffix++}";
                        var prefix = layout.Id[..Math.Min(layout.Id.Length, 64 - suffixText.Length)];
                        id = prefix + suffixText;
                    }
                    while (used.Contains(id));

                    renamed.Add(new LayoutRename(layout.Id, id));
                }

                used.Add(id);
                imported.Add(layout with { Id = id });
            }

            var next = _layouts.Concat(imported).ToArray();
            Persist(next, _settings);
            _layouts = next;
            return new(true, LayoutWriteResult.Success, imported.Count, renamed);
        }
    }

    public LayoutWriteResult UpdateSettings(LayoutSettings settings)
    {
        lock (_lock)
        {
            if (settings.ActivePresetId is { } id &&
                !BuiltInLayouts.All.Any(layout => layout.Id == id) &&
                !_layouts.Any(layout => layout.Id == id))
            {
                return LayoutWriteResult.UnknownPreset;
            }

            Persist(_layouts, settings);
            _settings = settings;
            return LayoutWriteResult.Success;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > MaximumFileSize)
            {
                throw new InvalidDataException("layouts.json exceeds the 2 MB limit");
            }

            var bytes = File.ReadAllBytes(_path);
            if (!TryParseDocument(bytes, out var layouts, out var settings))
            {
                throw new InvalidDataException("layouts.json has an invalid schema");
            }

            var migrated = false;
            _layouts = layouts!.Select(layout =>
            {
                if (layout.Id == "current" && layout.Name != "My layout")
                {
                    migrated = true;
                    return layout with { Name = "My layout" };
                }

                return layout;
            }).ToArray();
            _settings = settings!;

            if (migrated)
            {
                Persist(_layouts, _settings);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Quarantine();
            _layouts = [];
            _settings = new(null, null);
            SavedLayoutsCouldNotBeRead = true;
        }
    }

    private static bool TryParseDocument(
        ReadOnlyMemory<byte> bytes,
        out IReadOnlyList<Layout>? layouts,
        out LayoutSettings? settings)
    {
        layouts = null;
        settings = null;
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber != CurrentVersion ||
            !root.TryGetProperty("layouts", out var layoutsElement) ||
            layoutsElement.ValueKind != JsonValueKind.Array ||
            layoutsElement.GetArrayLength() > MaximumCustomLayouts)
        {
            return false;
        }

        string? activePresetId = null;
        if (root.TryGetProperty("activePresetId", out var activeElement))
        {
            if (activeElement.ValueKind == JsonValueKind.String)
            {
                activePresetId = activeElement.GetString();
            }
            else if (activeElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }

        string? temperatureUnit = null;
        if (root.TryGetProperty("temperatureUnit", out var unitElement))
        {
            if (unitElement.ValueKind == JsonValueKind.String)
            {
                temperatureUnit = unitElement.GetString();
                if (temperatureUnit is not ("C" or "F"))
                {
                    return false;
                }
            }
            else if (unitElement.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }

        var parsed = new List<Layout>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in layoutsElement.EnumerateArray())
        {
            if (!LayoutJson.TryParseLayout(element, out var layout) || !ids.Add(layout!.Id))
            {
                return false;
            }

            parsed.Add(layout);
        }

        if (activePresetId is not null &&
            !BuiltInLayouts.All.Any(layout => layout.Id == activePresetId) &&
            !ids.Contains(activePresetId))
        {
            activePresetId = null;
        }

        layouts = parsed;
        settings = new LayoutSettings(activePresetId, temperatureUnit);
        return true;
    }

    private void Persist(IReadOnlyList<Layout> layouts, LayoutSettings settings)
    {
        var document = new PersistedLayoutDocument(
            CurrentVersion,
            settings.ActivePresetId,
            settings.TemperatureUnit,
            layouts);

        using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, document, FileJsonOptions);
            stream.Flush(flushToDisk: true);
        }

        _beforeCommit?.Invoke();
        if (File.Exists(_path))
        {
            File.Replace(_tempPath, _path, _backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(_tempPath, _path, overwrite: true);
        }
    }

    private void Quarantine()
    {
        var unixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var directory = Path.GetDirectoryName(_path)!;
        var corruptPath = Path.Combine(directory, $"layouts.corrupt-{unixMs}.json");
        var suffix = 2;
        while (File.Exists(corruptPath))
        {
            corruptPath = Path.Combine(directory, $"layouts.corrupt-{unixMs}-{suffix++}.json");
        }

        File.Move(_path, corruptPath);
    }

    private sealed record PersistedLayoutDocument(
        int Version,
        string? ActivePresetId,
        string? TemperatureUnit,
        IReadOnlyList<Layout> Layouts);
}
