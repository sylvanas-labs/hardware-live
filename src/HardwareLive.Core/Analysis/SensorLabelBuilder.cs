using HardwareLive.Core.Classification;
using HardwareLive.Protocol;

// TextSanitizer lives in the root HardwareLive.Core namespace, not Analysis.
using HardwareLive.Core;

namespace HardwareLive.Core.Analysis;

/// <summary>A human-facing title (+ optional disambiguating subtitle) for one sensor, exposed
/// through <c>/api/meta</c>'s <c>labels</c> map (docs/SPEC.md step5-polish "Ambiguous
/// labels"). Every string here has already been through <see cref="TextSanitizer"/>: a raw
/// hardware/sensor name can contain control characters.</summary>
public sealed record SensorLabel(string Title, string? Subtitle);

/// <summary>
/// Builds a title (+ subtitle) for every sensor in a frame, replacing the raw LHM sensor
/// name the UI used to show verbatim -- the direct cause of tiles like two different "GPU
/// CORE" readings (one load, one clock), a bare "PACKAGE" tile, and SSD tiles that only say
/// "COMPOSITE TEMPERATURE" with no drive name.
///
/// Rule (docs/SPEC.md step5-polish):
///  - A sensor with a role gets that role's plain-English title (<see cref="RoleLabels"/>).
///    If more than one sensor currently shares that role (a multi-instance role, e.g.
///    per-DIMM temps or per-fan RPMs), it also gets a subtitle that disambiguates: the raw
///    sensor name when that already varies per instance (e.g. "DIMM #1", "Core #3", "GPU Fan
///    2"), or the hardware name when it doesn't (e.g. every NVMe's "Composite Temperature" is
///    identical, so the drive model disambiguates instead).
///  - An unclassified sensor (no role -- includes limit/metadata sensors like "Thermal Sensor
///    High Limit") gets its own sanitized sensor name as the title and the sanitized hardware
///    name as the subtitle, unconditionally.
/// </summary>
public static class SensorLabelBuilder
{
    public static IReadOnlyDictionary<string, SensorLabel> Build(SensorFrame? frame, ClassificationResult classification)
    {
        if (frame is null)
        {
            return new Dictionary<string, SensorLabel>(StringComparer.Ordinal);
        }

        var hardwareById = new Dictionary<string, HardwareInfo>(StringComparer.Ordinal);
        foreach (var hw in frame.Hardware)
        {
            hardwareById.TryAdd(hw.Id, hw);
        }

        var roleBySensorId = new Dictionary<string, SensorRole>(StringComparer.Ordinal);
        var sensorIdsByRole = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var role in classification.Roles)
        {
            roleBySensorId.TryAdd(role.SensorId, role);
            if (!sensorIdsByRole.TryGetValue(role.Role, out var list))
            {
                list = [];
                sensorIdsByRole[role.Role] = list;
            }

            list.Add(role.SensorId);
        }

        var namesByRole = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var sensor in frame.Sensors)
        {
            if (!roleBySensorId.TryGetValue(sensor.Id, out var role))
            {
                continue;
            }

            if (!namesByRole.TryGetValue(role.Role, out var names))
            {
                names = new Dictionary<string, string>(StringComparer.Ordinal);
                namesByRole[role.Role] = names;
            }

            names[sensor.Id] = TextSanitizer.StripControlCharacters(sensor.Name).Trim();
        }

        var labels = new Dictionary<string, SensorLabel>(StringComparer.Ordinal);
        foreach (var sensor in frame.Sensors)
        {
            if (labels.ContainsKey(sensor.Id))
            {
                continue; // Duplicate sensor id in an untrusted frame: first occurrence wins.
            }

            hardwareById.TryGetValue(sensor.HardwareId, out var hardware);
            var sanitizedHardwareName = hardware is null ? null : TextSanitizer.StripControlCharacters(hardware.Name).Trim();

            if (roleBySensorId.TryGetValue(sensor.Id, out var role))
            {
                var siblingIds = sensorIdsByRole[role.Role];
                string? subtitle = null;
                if (siblingIds.Count > 1)
                {
                    subtitle = DisambiguatingSubtitle(role.Role, sensor.Id, siblingIds, namesByRole, sanitizedHardwareName);
                }

                labels[sensor.Id] = new SensorLabel(RoleLabels.For(role.Role), subtitle);
            }
            else
            {
                var title = TextSanitizer.StripControlCharacters(sensor.Name).Trim();
                labels[sensor.Id] = new SensorLabel(title.Length > 0 ? title : sensor.Id, sanitizedHardwareName);
            }
        }

        return labels;
    }

    private static string? DisambiguatingSubtitle(
        string role,
        string sensorId,
        List<string> siblingIds,
        Dictionary<string, Dictionary<string, string>> namesByRole,
        string? sanitizedHardwareName)
    {
        // Storage sensor names never identify the drive ("Composite Temperature", "Life"):
        // even on a PC with exactly one NVMe and one SATA drive, where every name happens to
        // be unique across the two, "Composite Temperature" vs "Temperature" is exactly the
        // ambiguous-label bug this builder exists to fix. The drive model always
        // disambiguates instead, unconditionally, on any PC (docs/SPEC.md "arbitrary PC").
        if (role.StartsWith("storage.", StringComparison.Ordinal))
        {
            return sanitizedHardwareName;
        }

        if (!namesByRole.TryGetValue(role, out var names) || !names.TryGetValue(sensorId, out var ownName))
        {
            return sanitizedHardwareName;
        }

        // Elsewhere (DIMMs, per-core/per-fan sensors) the raw sensor name already varies per
        // instance and is shorter/more familiar than the full hardware name -- unless it
        // happens to be identical across siblings too, in which case the hardware name is
        // the only thing left that disambiguates.
        var nameIsShared = siblingIds.Count(id => names.TryGetValue(id, out var n) && n == ownName) > 1;
        if (nameIsShared)
        {
            return sanitizedHardwareName ?? (ownName.Length > 0 ? ownName : null);
        }

        return ownName.Length > 0 ? ownName : sanitizedHardwareName;
    }
}
