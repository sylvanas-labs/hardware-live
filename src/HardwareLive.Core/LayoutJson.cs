using System.Text.Json;

namespace HardwareLive.Core;

internal static class LayoutJson
{
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.Ordinal)
    {
        "id",
        "name",
        "widgets",
        "focus",
        "sort",
    };

    private static readonly HashSet<string> WidgetProperties = new(StringComparer.Ordinal)
    {
        "kind",
        "size",
        "ref",
        "series",
        "max",
    };

    private const int MaxChartSeries = 12;

    private static readonly HashSet<string> ReferenceProperties = new(StringComparer.Ordinal)
    {
        "role",
        "id",
        "hw",
    };

    private static readonly HashSet<string> ImportProperties = new(StringComparer.Ordinal)
    {
        "layouts",
    };

    private static readonly HashSet<string> SettingsProperties = new(StringComparer.Ordinal)
    {
        "activePresetId",
        "temperatureUnit",
        // FPS extensions (docs/SPEC.md step7-fps item 7): written straight through to
        // config.json's "fps" section, never to layouts.json -- see RequestRouter.HandleSettings.
        "fpsEnabled",
        "fpsDenylistAdd",
        "fpsPin",
    };

    private static readonly HashSet<string> WidgetKinds = new(StringComparer.Ordinal)
    {
        "tile",
        "chart",
        "gauge",
        "analysis",
        "notes",
    };

    private static readonly HashSet<string> WidgetSizes = new(StringComparer.Ordinal)
    {
        "S",
        "M",
        "L",
    };

    private static readonly HashSet<string> FocusValues = new(StringComparer.Ordinal)
    {
        "cpu",
        "gpu",
        "gaming",
    };

    private static readonly HashSet<string> SortValues = new(StringComparer.Ordinal)
    {
        "headroom",
    };

    public static bool TryParseLayout(ReadOnlyMemory<byte> json, out Layout? layout)
    {
        layout = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParseLayout(document.RootElement, out layout);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseImport(ReadOnlyMemory<byte> json, out IReadOnlyList<Layout>? layouts)
    {
        layouts = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyUniqueProperties(root, ImportProperties) ||
                !root.TryGetProperty("layouts", out var layoutsElement) ||
                layoutsElement.ValueKind != JsonValueKind.Array ||
                layoutsElement.GetArrayLength() > 100)
            {
                return false;
            }

            var parsed = new List<Layout>(layoutsElement.GetArrayLength());
            foreach (var layoutElement in layoutsElement.EnumerateArray())
            {
                if (!TryParseLayout(layoutElement, out var layout))
                {
                    return false;
                }

                parsed.Add(layout!);
            }

            layouts = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseSettings(ReadOnlyMemory<byte> json, out LayoutSettingsUpdate? update)
    {
        update = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasOnlyUniqueProperties(root, SettingsProperties) ||
                !root.EnumerateObject().Any())
            {
                return false;
            }

            var hasActivePresetId = root.TryGetProperty("activePresetId", out var activeElement);
            string? activePresetId = null;
            if (hasActivePresetId)
            {
                if (activeElement.ValueKind == JsonValueKind.String)
                {
                    activePresetId = activeElement.GetString();
                    if (activePresetId is null || !IsSyntacticallyValidId(activePresetId))
                    {
                        return false;
                    }
                }
                else if (activeElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            var hasTemperatureUnit = root.TryGetProperty("temperatureUnit", out var unitElement);
            string? temperatureUnit = null;
            if (hasTemperatureUnit)
            {
                if (unitElement.ValueKind != JsonValueKind.String ||
                    (temperatureUnit = unitElement.GetString()) is not ("C" or "F"))
                {
                    return false;
                }
            }

            var hasFpsEnabled = root.TryGetProperty("fpsEnabled", out var fpsEnabledElement);
            bool? fpsEnabled = null;
            if (hasFpsEnabled)
            {
                if (fpsEnabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                fpsEnabled = fpsEnabledElement.GetBoolean();
            }

            var hasFpsDenylistAdd = root.TryGetProperty("fpsDenylistAdd", out var denylistElement);
            string? fpsDenylistAdd = null;
            if (hasFpsDenylistAdd)
            {
                if (denylistElement.ValueKind != JsonValueKind.String ||
                    (fpsDenylistAdd = denylistElement.GetString()) is null ||
                    fpsDenylistAdd.Length is < 1 or > 260)
                {
                    return false;
                }
            }

            var hasFpsPin = root.TryGetProperty("fpsPin", out var pinElement);
            string? fpsPin = null;
            if (hasFpsPin)
            {
                if (pinElement.ValueKind == JsonValueKind.String)
                {
                    fpsPin = pinElement.GetString();
                    if (fpsPin is null || fpsPin.Length > 260)
                    {
                        return false;
                    }
                }
                else if (pinElement.ValueKind != JsonValueKind.Null)
                {
                    return false;
                }
            }

            update = new LayoutSettingsUpdate(
                hasActivePresetId,
                activePresetId,
                hasTemperatureUnit,
                temperatureUnit,
                hasFpsEnabled,
                fpsEnabled,
                hasFpsDenylistAdd,
                fpsDenylistAdd,
                hasFpsPin,
                fpsPin);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseLayout(JsonElement element, out Layout? layout)
    {
        layout = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(element, LayoutProperties) ||
            !TryGetString(element, "id", out var id) ||
            !IsValidLayoutId(id) ||
            !TryGetString(element, "name", out var name) ||
            name.Length is < 1 or > 100 ||
            !element.TryGetProperty("widgets", out var widgetsElement) ||
            widgetsElement.ValueKind != JsonValueKind.Array ||
            widgetsElement.GetArrayLength() > 500)
        {
            return false;
        }

        var widgets = new List<LayoutWidget>(widgetsElement.GetArrayLength());
        foreach (var widgetElement in widgetsElement.EnumerateArray())
        {
            if (!TryParseWidget(widgetElement, out var widget))
            {
                return false;
            }

            widgets.Add(widget!);
        }

        if (!TryGetOptionalEnum(element, "focus", FocusValues, out var focus) ||
            !TryGetOptionalEnum(element, "sort", SortValues, out var sort))
        {
            return false;
        }

        layout = new Layout(id, name, widgets, focus, sort);
        return true;
    }

    private static bool TryParseWidget(JsonElement element, out LayoutWidget? widget)
    {
        widget = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(element, WidgetProperties) ||
            !TryGetString(element, "kind", out var kind) ||
            !WidgetKinds.Contains(kind) ||
            !TryGetString(element, "size", out var size) ||
            !WidgetSizes.Contains(size))
        {
            return false;
        }

        var hasRef = element.TryGetProperty("ref", out var referenceElement);
        var hasSeries = element.TryGetProperty("series", out var seriesElement);
        var hasMax = element.TryGetProperty("max", out var maxElement);

        // Only a chart may use series/max, and only a chart may omit ref (in favor of series).
        if (kind != "chart")
        {
            if (hasSeries || hasMax || !hasRef || !TryParseReference(referenceElement, out var plainReference))
            {
                return false;
            }

            widget = new LayoutWidget(kind, size, plainReference);
            return true;
        }

        // A chart carries exactly one of ref (single series) or series (multi-series).
        if (hasRef == hasSeries)
        {
            return false;
        }

        double? max = null;
        if (hasMax)
        {
            if (maxElement.ValueKind != JsonValueKind.Number ||
                !maxElement.TryGetDouble(out var maxValue) ||
                !double.IsFinite(maxValue) ||
                maxValue <= 0)
            {
                return false;
            }

            max = maxValue;
        }

        if (hasRef)
        {
            if (!TryParseReference(referenceElement, out var reference))
            {
                return false;
            }

            widget = new LayoutWidget(kind, size, reference, Series: null, Max: max);
            return true;
        }

        if (seriesElement.ValueKind != JsonValueKind.Array ||
            seriesElement.GetArrayLength() is 0 or > MaxChartSeries)
        {
            return false;
        }

        var series = new List<LayoutReference>(seriesElement.GetArrayLength());
        foreach (var seriesItem in seriesElement.EnumerateArray())
        {
            if (!TryParseReference(seriesItem, out var seriesReference))
            {
                return false;
            }

            series.Add(seriesReference!);
        }

        widget = new LayoutWidget(kind, size, Ref: null, Series: series, Max: max);
        return true;
    }

    private static bool TryParseReference(JsonElement element, out LayoutReference? reference)
    {
        reference = null;
        if (element.ValueKind != JsonValueKind.Object || !HasOnlyUniqueProperties(element, ReferenceProperties))
        {
            return false;
        }

        var hasRole = TryGetOptionalString(element, "role", out var role);
        var hasId = TryGetOptionalString(element, "id", out var id);
        var hasHardware = TryGetOptionalString(element, "hw", out var hardware);
        var propertyCount = element.EnumerateObject().Count();

        if (propertyCount == 1 && hasRole)
        {
            reference = new LayoutReference(role, null, null);
            return true;
        }

        if (hasId && hasHardware && propertyCount == (hasRole ? 3 : 2))
        {
            reference = new LayoutReference(role, id, hardware);
            return true;
        }

        return false;
    }

    // "import" is reserved: /api/layouts/import is routed to the import handler,
    // so a layout with that ID could never be updated or deleted.
    internal static bool IsReservedLayoutId(string id) =>
        id.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidLayoutId(string id) =>
        IsSyntacticallyValidId(id) &&
        !id.Equals("import", StringComparison.OrdinalIgnoreCase) &&
        !IsReservedLayoutId(id);

    private static bool IsSyntacticallyValidId(string id) =>
        id.Length is >= 1 and <= 64 &&
        id.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool HasOnlyUniqueProperties(JsonElement element, HashSet<string> allowedProperties)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            (value = property.GetString()!) is not null;
    }

    private static bool TryGetOptionalString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static bool TryGetOptionalEnum(
        JsonElement element,
        string propertyName,
        IReadOnlySet<string> allowed,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null && allowed.Contains(value);
    }
}

internal sealed record LayoutSettingsUpdate(
    bool HasActivePresetId,
    string? ActivePresetId,
    bool HasTemperatureUnit,
    string? TemperatureUnit,
    bool HasFpsEnabled = false,
    bool? FpsEnabled = null,
    bool HasFpsDenylistAdd = false,
    string? FpsDenylistAdd = null,
    bool HasFpsPin = false,
    string? FpsPin = null);
