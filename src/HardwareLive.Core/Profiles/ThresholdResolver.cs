using System.Text.RegularExpressions;
using HardwareLive.Core.Classification;
using HardwareLive.Protocol;

namespace HardwareLive.Core.Profiles;

/// <summary>
/// Resolves a watch/critical pair for every temperature-role sensor in a frame, per
/// docs/SPEC.md's "Threshold resolution" order (first match wins): user override, then
/// device-reported limits, then the vendor profile table, then a generic community
/// fallback. A tier that supplies only one of watch/critical leaves the other half to the
/// next tier down (documented ambiguity: the spec's worked examples are all full pairs).
/// Pure function of its inputs; never throws on untrusted hardware/sensor names.
/// </summary>
public static class ThresholdResolver
{
    /// <summary>Roles that ever get a threshold. The per-core/per-CCD/per-sensor detail
    /// roles (cpu.temp.ccd/core/max/avg, storage.temp.sensor) intentionally have none: the
    /// spec's profile and generic tables don't cover them, and borrowing cpu.temp.control's
    /// numbers for e.g. a single CCD would be a guess, not a spec value.</summary>
    private static readonly HashSet<string> TemperatureRoles = new(StringComparer.Ordinal)
    {
        Roles.CpuTempControl,
        Roles.GpuTempCore,
        Roles.GpuTempHotspot,
        Roles.GpuTempMem,
        Roles.IgpuTempCore,
        Roles.BoardTempVrm,
        Roles.BoardTempChipset,
        Roles.BoardTempPcie,
        Roles.BoardTempSystem,
        Roles.BoardTempCpu,
        Roles.DimmTemp,
        Roles.StorageTemp,
        Roles.BatteryTemp,
    };

    private static readonly Dictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);
    private static readonly object RegexLock = new();

    public static ThresholdResolution Resolve(SensorFrame? frame, ClassificationResult classification, UserThresholdConfig overrides)
    {
        if (frame is null)
        {
            return ThresholdResolution.Empty;
        }

        var hardwareById = new Dictionary<string, HardwareInfo>(StringComparer.Ordinal);
        foreach (var hw in frame.Hardware)
        {
            hardwareById.TryAdd(hw.Id, hw);
        }

        var valueBySensorId = new Dictionary<string, float?>(StringComparer.Ordinal);
        foreach (var sensor in frame.Sensors)
        {
            valueBySensorId.TryAdd(sensor.Id, sensor.Value);
        }

        var deviceLimitsByTarget = new Dictionary<string, List<LimitSensor>>(StringComparer.Ordinal);
        foreach (var limit in classification.Limits)
        {
            if (limit.Kind is LimitKinds.TjmaxDistance or LimitKinds.Low)
            {
                continue;
            }

            if (!deviceLimitsByTarget.TryGetValue(limit.AppliesTo, out var list))
            {
                list = [];
                deviceLimitsByTarget[limit.AppliesTo] = list;
            }

            list.Add(limit);
        }

        var table = ThresholdProfiles.Table;
        var cpuName = classification.PrimaryCpuId is { } cpuId && hardwareById.TryGetValue(cpuId, out var cpuHw) ? cpuHw.Name : null;
        var gpuName = classification.PrimaryGpuId is { } gpuId && hardwareById.TryGetValue(gpuId, out var gpuHw) ? gpuHw.Name : null;
        var cpuProfile = cpuName is not null ? MatchProfile(table.Cpu, cpuName) : null;
        var gpuProfile = gpuName is not null ? MatchProfile(table.Gpu, gpuName) : null;

        var thresholds = new Dictionary<string, ThresholdEntry>(StringComparer.Ordinal);
        var ignoredOverrides = new List<IgnoredOverride>();
        foreach (var role in classification.Roles)
        {
            if (!TemperatureRoles.Contains(role.Role))
            {
                continue;
            }

            var (entry, ignored) = ResolveOne(
                role,
                overrides,
                deviceLimitsByTarget,
                valueBySensorId,
                classification.PrimaryCpuId,
                classification.PrimaryGpuId,
                cpuProfile,
                gpuProfile,
                table);

            if (entry is not null)
            {
                thresholds[role.SensorId] = entry;
            }

            if (ignored is not null)
            {
                ignoredOverrides.Add(ignored);
            }
        }

        return new ThresholdResolution(thresholds, cpuProfile?.Name, gpuProfile?.Name) { IgnoredOverrides = ignoredOverrides };
    }

    private static (ThresholdEntry? Entry, IgnoredOverride? Ignored) ResolveOne(
        SensorRole role,
        UserThresholdConfig overrides,
        Dictionary<string, List<LimitSensor>> deviceLimitsByTarget,
        Dictionary<string, float?> valueBySensorId,
        string? primaryCpuId,
        string? primaryGpuId,
        ProfileEntry? cpuProfile,
        ProfileEntry? gpuProfile,
        ProfileTable table)
    {
        var (baseWatch, baseCritical, source, confidence, baseOrigin) = ResolveBase(
            role, deviceLimitsByTarget, valueBySensorId, primaryCpuId, primaryGpuId, cpuProfile, gpuProfile, table);

        if (!TryGetOverride(role, overrides, out var overrideValue))
        {
            return (BuildEntry(baseWatch, baseCritical, source, confidence, baseOrigin), null);
        }

        var candidateWatch = overrideValue.Watch ?? baseWatch;
        var candidateCritical = overrideValue.Critical ?? baseCritical;

        if (IsValidPair(candidateWatch, candidateCritical))
        {
            return (BuildEntry(candidateWatch, candidateCritical, source, confidence, ThresholdOrigin.Override), null);
        }

        // Invariant: a partial override must never remove monitoring. If the override,
        // merged with the fully resolved base (device/profile/generic), would produce an
        // invalid watch/critical pair, drop the override entirely for this sensor and
        // keep the base threshold instead -- but still surface that it happened.
        var baseEntry = BuildEntry(baseWatch, baseCritical, source, confidence, baseOrigin);
        var reason = $"override (watch={FormatOrNull(overrideValue.Watch)}, critical={FormatOrNull(overrideValue.Critical)}) " +
            $"merged with the base threshold (watch={FormatOrNull(baseWatch)}, critical={FormatOrNull(baseCritical)}) is invalid; kept the base threshold";
        return (baseEntry, new IgnoredOverride(role.SensorId, reason));
    }

    private static ThresholdEntry? BuildEntry(double? watch, double? critical, string source, string confidence, string origin) =>
        IsValidPair(watch, critical) ? new ThresholdEntry(watch!.Value, critical!.Value, source, confidence, origin) : null;

    private static bool IsValidPair(double? watch, double? critical) =>
        watch is not null && critical is not null && watch > 0 && watch <= critical && critical <= 150;

    private static string FormatOrNull(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static (double? Watch, double? Critical, string Source, string Confidence, string Origin) ResolveBase(
        SensorRole role,
        Dictionary<string, List<LimitSensor>> deviceLimitsByTarget,
        Dictionary<string, float?> valueBySensorId,
        string? primaryCpuId,
        string? primaryGpuId,
        ProfileEntry? cpuProfile,
        ProfileEntry? gpuProfile,
        ProfileTable table)
    {
        double? watch = null;
        double? critical = null;
        string? source = null;
        string? confidence = null;
        string? origin = null;

        if (deviceLimitsByTarget.TryGetValue(role.SensorId, out var deviceLimits))
        {
            var (dw, dc, hasAny) = ResolveDeviceLimit(deviceLimits, valueBySensorId);
            if (hasAny)
            {
                watch ??= dw;
                critical ??= dc;
                source ??= "device";
                confidence ??= ThresholdConfidence.Vendor;
                origin ??= ThresholdOrigin.Device;
            }
        }

        if (watch is null || critical is null)
        {
            var profile = role.HardwareId == primaryCpuId ? cpuProfile
                : role.HardwareId == primaryGpuId ? gpuProfile
                : null;
            var limit = profile?.Limits.FirstOrDefault(l => l.Role == role.Role);
            if (limit is not null)
            {
                var (lw, lc) = limit.Resolve();
                watch ??= lw;
                critical ??= lc;
                source ??= limit.Source ?? profile!.Source;
                confidence ??= limit.Confidence ?? profile!.Confidence;
                origin ??= ThresholdOrigin.Profile;
            }
        }

        if (watch is null || critical is null)
        {
            var generic = table.Generic.FirstOrDefault(l => l.Role == role.Role);
            if (generic is not null)
            {
                var (gw, gc) = generic.Resolve();
                watch ??= gw;
                critical ??= gc;
                source ??= generic.Source;
                confidence ??= generic.Confidence;
                origin ??= ThresholdOrigin.Generic;
            }
        }

        return (watch, critical, source ?? "community", confidence ?? ThresholdConfidence.Community, origin ?? ThresholdOrigin.Generic);
    }

    /// <summary>NVMe (warning/critical) and DIMM (high/critical) device-limit rules, per
    /// docs/SPEC.md step 2. Distinguished by which limit kinds are present: only NVMe ever
    /// emits <see cref="LimitKinds.Warning"/>, only DIMM ever emits <see cref="LimitKinds.High"/>
    /// (see SensorClassifier.Storage.cs / SensorClassifier.Board.cs); a target with only a
    /// <see cref="LimitKinds.Critical"/> reading (either family, e.g. a drive/DIMM missing
    /// its paired limit) contributes critical only, leaving watch to the next tier.</summary>
    private static (double? Watch, double? Critical, bool HasAny) ResolveDeviceLimit(
        List<LimitSensor> limits, Dictionary<string, float?> valueBySensorId)
    {
        var warning = FirstValidValue(limits, LimitKinds.Warning, valueBySensorId);
        var critical = FirstValidValue(limits, LimitKinds.Critical, valueBySensorId);
        var high = FirstValidValue(limits, LimitKinds.High, valueBySensorId);

        if (warning is not null)
        {
            var watch = warning.Value - 5;
            var resolvedCritical = warning.Value;
            if (critical is not null && critical.Value < warning.Value + 10)
            {
                resolvedCritical = Math.Min(warning.Value, critical.Value);
            }

            return (watch, resolvedCritical, true);
        }

        if (high is not null)
        {
            return (high, critical, true);
        }

        if (critical is not null)
        {
            return (null, critical, true);
        }

        return (null, null, false);
    }

    private static double? FirstValidValue(List<LimitSensor> limits, string kind, Dictionary<string, float?> valueBySensorId)
    {
        foreach (var limit in limits)
        {
            if (limit.Kind != kind)
            {
                continue;
            }

            if (valueBySensorId.TryGetValue(limit.SensorId, out var reading) &&
                reading is { } value &&
                IsValidLimit(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsValidLimit(double value) => double.IsFinite(value) && value > 0 && value <= 150;

    private static bool TryGetOverride(SensorRole role, UserThresholdConfig overrides, out ThresholdOverride value)
    {
        if (overrides.TryGet(role.SensorId, out value))
        {
            return true;
        }

        if (overrides.TryGet(role.Role, out value))
        {
            return true;
        }

        value = new ThresholdOverride(null, null);
        return false;
    }

    private static ProfileEntry? MatchProfile(IReadOnlyList<ProfileEntry> entries, string hardwareName)
    {
        foreach (var entry in entries)
        {
            if (SafeIsMatch(entry.Pattern, hardwareName))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool SafeIsMatch(string pattern, string input)
    {
        try
        {
            return GetRegex(pattern).IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex GetRegex(string pattern)
    {
        lock (RegexLock)
        {
            if (!RegexCache.TryGetValue(pattern, out var regex))
            {
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                RegexCache[pattern] = regex;
            }

            return regex;
        }
    }
}
