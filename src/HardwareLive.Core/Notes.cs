using System.Diagnostics;
using System.Text.Json;

namespace HardwareLive.Core;

/// <summary>One validated notes payload (docs/SPEC.md Component 7). <see cref="Lines"/> and
/// <see cref="Source"/> have already had C0 control characters stripped -- the file is
/// untrusted input, written by any tool the user points at it.</summary>
public sealed record NotesPayload(string At, long Ts, string? Source, IReadOnlyList<string> Lines);

/// <summary>
/// Reads and validates <c>%LOCALAPPDATA%\HardwareLive\notes.json</c> (docs/SPEC.md Component 7
/// / step5 feature 0). This is optional, untrusted, third-party input: a missing file is the
/// normal case (no AI tool in use, Invariant 5), and any malformed file is treated exactly like
/// an absent one -- logged, never thrown, never crashes the request pipeline.
/// </summary>
public static class NotesReader
{
    public const int MaxFileBytes = 16 * 1024;
    public const int MaxLines = 20;
    public const int MaxLineChars = 500;
    private const int MaxAtChars = 200;
    private const int MaxSourceChars = 100;

    // DateTimeOffset.FromUnixTimeSeconds' valid range, so the router's ageMinutes calc can
    // never throw on an untrusted (or corrupted) ts value.
    private const long MinUnixSeconds = -62135596800;
    private const long MaxUnixSeconds = 253402300799;

    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "at", "ts", "source", "lines",
    };

    public static NotesPayload? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Read one byte past the cap: a file that's exactly at the cap is fine, one that's
            // larger is rejected without trusting FileInfo.Length (which can race a concurrent
            // writer between the existence check and the read).
            var buffer = new byte[MaxFileBytes + 1];
            var totalRead = 0;
            int read;
            while (totalRead < buffer.Length && (read = stream.Read(buffer, totalRead, buffer.Length - totalRead)) > 0)
            {
                totalRead += read;
            }

            if (totalRead > MaxFileBytes)
            {
                Trace.TraceWarning("HardwareLive notes.json rejected: larger than {0} bytes.", MaxFileBytes);
                return null;
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, totalRead));
            return ParsePayload(document.RootElement);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("HardwareLive notes.json rejected: {0}", exception.Message);
            return null;
        }
    }

    private static NotesPayload? ParsePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !HasOnlyAllowedProperties(root))
        {
            return null;
        }

        if (!root.TryGetProperty("at", out var atElement) ||
            atElement.ValueKind != JsonValueKind.String ||
            atElement.GetString() is not { Length: > 0 and <= MaxAtChars } at)
        {
            return null;
        }

        if (!root.TryGetProperty("ts", out var tsElement) ||
            tsElement.ValueKind != JsonValueKind.Number ||
            !tsElement.TryGetInt64(out var ts) ||
            ts < MinUnixSeconds || ts > MaxUnixSeconds)
        {
            return null;
        }

        string? source = null;
        if (root.TryGetProperty("source", out var sourceElement))
        {
            if (sourceElement.ValueKind != JsonValueKind.String ||
                sourceElement.GetString() is not { Length: > 0 and <= MaxSourceChars } sourceValue)
            {
                return null;
            }

            source = TextSanitizer.StripControlCharacters(sourceValue);
        }

        if (!root.TryGetProperty("lines", out var linesElement) ||
            linesElement.ValueKind != JsonValueKind.Array ||
            linesElement.GetArrayLength() > MaxLines)
        {
            return null;
        }

        var lines = new List<string>(linesElement.GetArrayLength());
        foreach (var lineElement in linesElement.EnumerateArray())
        {
            if (lineElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var line = lineElement.GetString() ?? string.Empty;
            if (line.Length > MaxLineChars)
            {
                return null;
            }

            lines.Add(TextSanitizer.StripControlCharacters(line));
        }

        return new NotesPayload(TextSanitizer.StripControlCharacters(at), ts, source, lines);
    }

    private static bool HasOnlyAllowedProperties(JsonElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }
}
