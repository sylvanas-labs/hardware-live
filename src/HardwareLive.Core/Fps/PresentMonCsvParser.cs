using System.Globalization;

namespace HardwareLive.Core.Fps;

/// <summary>One PresentMon v2 stdout row that actually matters to the aggregator/target
/// selector (docs/SPEC.md Component 9). Every other v2 column is ignored on purpose --
/// parsing by header name only, never by column index, so a future PresentMon column
/// reorder can't silently shift values into the wrong field.</summary>
public sealed record PresentMonRow(
    string Application,
    int ProcessId,
    string PresentMode,
    double CpuStartQpcTimeMs,
    double? FrameTimeMs);

/// <summary>
/// Header-driven parser for PresentMon's <c>--v2_metrics --qpc_time_ms</c> stdout CSV
/// (docs/SPEC.md Component 9, verified against the real v2.6.0 binary): plain ASCII, LF line
/// endings, <c>NA</c>/empty cells mean "no value", and there is no <c>MsBetweenPresents</c>
/// column in v2 mode. Never throws on untrusted process output: a malformed or oversize line
/// is simply dropped (<see cref="TryParseRow"/> returns false), matching PresentMonRunner's
/// "bounded line length, drop excess" rule.
/// </summary>
public static class PresentMonCsvParser
{
    /// <summary>Lines longer than this (untrusted process stdout) are dropped rather than
    /// parsed (docs/SPEC.md step7-fps: "bounded line length 4 KB").</summary>
    public const int MaxLineLength = 4096;

    private static readonly string[] RequiredColumns = ["ProcessID", "CPUStartQPCTime", "FrameTime"];

    /// <summary>
    /// Parses the header line into a column-name -&gt; index map. Returns false (an
    /// "unexpected PresentMon output" condition per the spec) when any of
    /// <see cref="RequiredColumns"/> is missing.
    /// </summary>
    public static bool TryParseHeader(string headerLine, out IReadOnlyDictionary<string, int> columns)
    {
        columns = ImmutableColumnsEmpty;
        if (string.IsNullOrEmpty(headerLine) || headerLine.Length > MaxLineLength)
        {
            return false;
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var fields = headerLine.Split(',');
        for (var i = 0; i < fields.Length; i++)
        {
            var name = fields[i].Trim();
            if (name.Length > 0)
            {
                map.TryAdd(name, i);
            }
        }

        foreach (var required in RequiredColumns)
        {
            if (!map.ContainsKey(required))
            {
                columns = ImmutableColumnsEmpty;
                return false;
            }
        }

        columns = map;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, int> ImmutableColumnsEmpty =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Parses one data row against a header previously validated by
    /// <see cref="TryParseHeader"/>. Returns false for an oversize line, a line with too few
    /// fields for the required columns, or a ProcessID that doesn't parse as an integer.
    /// A non-numeric/NA/empty FrameTime yields <see cref="PresentMonRow.FrameTimeMs"/> of
    /// null rather than failing the whole row (the caller -- <see cref="Fps.FpsAggregator"/>
    /// -- is the one that skips NA/&lt;=0 frames per the spec).
    /// </summary>
    public static bool TryParseRow(string line, IReadOnlyDictionary<string, int> columns, out PresentMonRow? row)
    {
        row = null;
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineLength)
        {
            return false;
        }

        var fields = line.Split(',');
        var requiredMaxIndex = 0;
        foreach (var required in RequiredColumns)
        {
            if (!columns.TryGetValue(required, out var index))
            {
                return false;
            }

            requiredMaxIndex = Math.Max(requiredMaxIndex, index);
        }

        if (fields.Length <= requiredMaxIndex)
        {
            return false;
        }

        if (!TryGetInt(fields, columns, "ProcessID", out var processId))
        {
            return false;
        }

        if (!TryGetDouble(fields, columns, "CPUStartQPCTime", out var qpc) || qpc is null)
        {
            return false;
        }

        TryGetDouble(fields, columns, "FrameTime", out var frameTime);

        var application = TryGetField(fields, columns, "Application", out var app) ? app : string.Empty;
        var presentMode = TryGetField(fields, columns, "PresentMode", out var mode) ? mode : string.Empty;

        row = new PresentMonRow(application, processId, presentMode, qpc.Value, frameTime);
        return true;
    }

    private static bool TryGetField(string[] fields, IReadOnlyDictionary<string, int> columns, string name, out string value)
    {
        value = string.Empty;
        if (!columns.TryGetValue(name, out var index) || index >= fields.Length)
        {
            return false;
        }

        value = fields[index].Trim();
        return true;
    }

    private static bool TryGetInt(string[] fields, IReadOnlyDictionary<string, int> columns, string name, out int value)
    {
        value = 0;
        return TryGetField(fields, columns, name, out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDouble(string[] fields, IReadOnlyDictionary<string, int> columns, string name, out double? value)
    {
        value = null;
        if (!TryGetField(fields, columns, name, out var text) || text.Length == 0 ||
            string.Equals(text, "NA", StringComparison.Ordinal))
        {
            return true;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
