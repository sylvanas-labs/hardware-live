using System.Text.RegularExpressions;
using HardwareLive.Core;

namespace HardwareLive.Tests;

/// <summary>
/// docs/SPEC.md Invariant 5: "AI is optional, never required" -- the shipped UI never names
/// an AI product on its own. This greps the actual embedded resources the assembly ships
/// (not the source tree, so it proves what a build really serves), case-sensitively, so it
/// never trips on lowercase words like "main", "await" or "aria" that merely contain the
/// letters.
/// </summary>
public sealed partial class InvariantFiveShippedTextTests
{
    [GeneratedRegex(@"Claude|Codex|\bAI\b")]
    private static partial Regex ForbiddenMention();

    [Fact]
    public void NoShippedWwwrootResourceMentionsAnAiProductOrGenericAi()
    {
        var assembly = typeof(HardwareLiveServer).Assembly;
        var wwwrootResources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("HardwareLive.Core.wwwroot.", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(wwwrootResources);

        foreach (var resourceName in wwwrootResources)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var match = ForbiddenMention().Match(content);
            Assert.False(match.Success, $"{resourceName} mentions a forbidden term near: \"{Excerpt(content, match)}\"");
        }
    }

    [Fact]
    public void TheAnalysisWidgetIsTitledHealthAnalysis()
    {
        var assembly = typeof(HardwareLiveServer).Assembly;
        using var stream = assembly.GetManifestResourceStream("HardwareLive.Core.wwwroot.js.widgets.js")!;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        Assert.Contains("Health analysis", content, StringComparison.Ordinal);
    }

    private static string Excerpt(string content, Match match)
    {
        if (!match.Success)
        {
            return string.Empty;
        }

        var start = Math.Max(0, match.Index - 20);
        var length = Math.Min(content.Length - start, 60);
        return content.Substring(start, length);
    }
}
