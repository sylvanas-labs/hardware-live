using System.Globalization;
using System.Text.RegularExpressions;

namespace HardwareLive.Core.Analysis;

internal static partial class HealthResponsePresenter
{
    private const string RecoveryMessage = "saved layouts could not be read; a backup was kept";

    public static PresentedHealth Present(
        AnalysisResult result,
        string temperatureUnit,
        bool includeLayoutRecoveryConcern,
        double uptimeSeconds)
    {
        var concerns = result.Concerns
            .Select(concern => concern with { Message = ConvertTemperatureText(concern.Message, temperatureUnit) })
            .ToList();
        if (includeLayoutRecoveryConcern)
        {
            concerns.Add(RecoveryConcern());
        }

        var rateFactor = temperatureUnit == "F" ? 9.0 / 5.0 : 1.0;
        var rateUnit = temperatureUnit == "F" ? "°F/min" : "°C/min";
        var trends = result.Trends.Select(trend => new PresentedTrend(
            trend.SensorId,
            trend.Role,
            trend.SlopePerMin * rateFactor,
            trend.EtaMinutes,
            trend.Label,
            rateUnit)).ToArray();

        return new PresentedHealth(
            result.Status,
            result.Reason,
            ConvertTemperatureText(result.Headline, temperatureUnit),
            result.Phase,
            concerns,
            trends,
            uptimeSeconds,
            result.SensorLevels);
    }

    public static IReadOnlyList<Concern> RecoveryConcerns(bool include) =>
        include ? [RecoveryConcern()] : [];

    internal static string ConvertTemperatureText(string text, string temperatureUnit)
    {
        var fahrenheit = temperatureUnit == "F";
        var rateConverted = RateRegex().Replace(text, match =>
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var converted = fahrenheit ? value * 9.0 / 5.0 : value;
            return $"{converted.ToString("0.0", CultureInfo.InvariantCulture)} °{temperatureUnit}/min";
        });

        var absoluteConverted = AbsoluteRegex().Replace(rateConverted, match =>
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var converted = fahrenheit ? (value * 9.0 / 5.0) + 32 : value;
            var prefix = rateConverted[..match.Index];
            var threshold = prefix.EndsWith("critical (", StringComparison.Ordinal) ||
                prefix.EndsWith("watch (", StringComparison.Ordinal) ||
                prefix.EndsWith("to hit ", StringComparison.Ordinal);
            var format = threshold ? "0" : "0.0";
            return $"{converted.ToString(format, CultureInfo.InvariantCulture)} °{temperatureUnit}";
        });

        return DegreesFromLimitRegex().Replace(absoluteConverted, match =>
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var converted = fahrenheit ? value * 9.0 / 5.0 : value;
            return $"{converted.ToString("0.0", CultureInfo.InvariantCulture)} degrees from limit";
        });
    }

    private static Concern RecoveryConcern() =>
        new(ConcernLevel.Info, "layouts", "layouts.json", RecoveryMessage);

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?) °C/min", RegexOptions.CultureInvariant)]
    private static partial Regex RateRegex();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?) °C(?!/min)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteRegex();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?) degrees from limit", RegexOptions.CultureInvariant)]
    private static partial Regex DegreesFromLimitRegex();
}

internal sealed record PresentedTrend(
    string SensorId,
    string Role,
    double SlopePerMin,
    double? EtaMinutes,
    string Label,
    string Unit);

/// <summary><see cref="SensorLevels"/> serializes as <c>/api/health</c>'s <c>sensorLevels</c>
/// (docs/SPEC.md analysis rule 2 / step5-polish "Tiles consistency"): the UI prefers this
/// server-computed per-sensor level over its own local threshold comparison, falling back only
/// for a sensor missing from the map.</summary>
internal sealed record PresentedHealth(
    HealthStatus Status,
    string? Reason,
    string Headline,
    string Phase,
    IReadOnlyList<Concern> Concerns,
    IReadOnlyList<PresentedTrend> Trends,
    double UptimeSeconds,
    IReadOnlyDictionary<string, string> SensorLevels);
