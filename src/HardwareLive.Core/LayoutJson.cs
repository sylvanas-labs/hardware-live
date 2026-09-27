using System.Text.Json;

namespace HardwareLive.Core;

internal static class LayoutJson
{
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.Ordinal)
    {
        "id",
        "name",
        "widgets",
    };

    private static readonly HashSet<string> WidgetProperties = new(StringComparer.Ordinal)
    {
        "kind",
        "size",
        "ref",
    };

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

    private static bool TryParseLayout(JsonElement element, out Layout? layout)
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

        layout = new Layout(id, name, widgets);
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
            !WidgetSizes.Contains(size) ||
            !element.TryGetProperty("ref", out var referenceElement) ||
            !TryParseReference(referenceElement, out var reference))
        {
            return false;
        }

        widget = new LayoutWidget(kind, size, reference!);
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
    private static bool IsValidLayoutId(string id) =>
        id.Length is >= 1 and <= 64 &&
        !id.Equals("import", StringComparison.OrdinalIgnoreCase) &&
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
}
