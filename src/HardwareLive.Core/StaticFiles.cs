namespace HardwareLive.Core;

/// <summary>
/// The explicit allow-list of static assets the loopback server will ever serve
/// (docs/SPEC.md step5: "Static files from an explicit allow-list only"). Every entry is an
/// embedded resource baked into the assembly at build time -- there is no filesystem read at
/// request time, so there is nothing for a `/../` path to traverse into. Any path not in this
/// dictionary falls through to a 404, whatever it looks like.
/// </summary>
internal static class StaticFiles
{
    public const string IndexHtmlResourceName = "HardwareLive.Core.wwwroot.index.html";
    private const string JsContentType = "text/javascript; charset=utf-8";

    private static readonly Dictionary<string, (string ResourceName, string ContentType)> Files =
        new(StringComparer.Ordinal)
        {
            ["/app.css"] = ("HardwareLive.Core.wwwroot.app.css", "text/css; charset=utf-8"),
            ["/js/logic.js"] = ("HardwareLive.Core.wwwroot.js.logic.js", JsContentType),
            ["/js/dom.js"] = ("HardwareLive.Core.wwwroot.js.dom.js", JsContentType),
            ["/js/api.js"] = ("HardwareLive.Core.wwwroot.js.api.js", JsContentType),
            ["/js/widgets.js"] = ("HardwareLive.Core.wwwroot.js.widgets.js", JsContentType),
            ["/js/dragdrop.js"] = ("HardwareLive.Core.wwwroot.js.dragdrop.js", JsContentType),
            ["/js/picker.js"] = ("HardwareLive.Core.wwwroot.js.picker.js", JsContentType),
            ["/js/app.js"] = ("HardwareLive.Core.wwwroot.js.app.js", JsContentType),
        };

    public static bool TryGetResource(string path, out string resourceName, out string contentType)
    {
        if (Files.TryGetValue(path, out var entry))
        {
            resourceName = entry.ResourceName;
            contentType = entry.ContentType;
            return true;
        }

        resourceName = string.Empty;
        contentType = string.Empty;
        return false;
    }
}
