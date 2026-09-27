using System.Text.RegularExpressions;
using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

/// <summary>
/// Maps a <see cref="SensorFrame"/>'s sensors onto the fixed role vocabulary in
/// docs/SPEC.md (Component 3 / classifier acceptance criteria). Pure function of the
/// frame: never throws on untrusted hardware/sensor names (they can contain junk bytes
/// from SPD or garbled firmware strings), and ignores sensors whose hardwareId has no
/// matching hardware entry.
///
/// Classification order, per spec: hardware Type, then sensor Type, then the stable
/// Identifier path (used here to distinguish AMD/Intel CPUs and NVMe/SATA storage), and
/// names only as the last fallback (case-insensitively, trimmed).
/// </summary>
public static partial class SensorClassifier
{
    internal delegate void AddRoleFn(SensorReading sensor, string role, Confidence confidence, int? instance = null);

    public static ClassificationResult Classify(SensorFrame? frame)
    {
        if (frame is null)
        {
            return ClassificationResult.Empty;
        }

        var hardwareById = new Dictionary<string, HardwareInfo>(StringComparer.Ordinal);
        var orderedHardware = new List<HardwareInfo>();
        foreach (var hw in frame.Hardware)
        {
            if (hardwareById.TryAdd(hw.Id, hw))
            {
                orderedHardware.Add(hw);
            }
        }

        var sensorsByHardware = new Dictionary<string, List<SensorReading>>(StringComparer.Ordinal);
        var seenSensorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sensor in frame.Sensors)
        {
            // Ignore sensors whose hardwareId has no hardware entry, and (real-world
            // fixtures show this happens) a duplicated sensor id: first occurrence wins.
            if (!hardwareById.ContainsKey(sensor.HardwareId) || !seenSensorIds.Add(sensor.Id))
            {
                continue;
            }

            if (!sensorsByHardware.TryGetValue(sensor.HardwareId, out var list))
            {
                list = [];
                sensorsByHardware[sensor.HardwareId] = list;
            }

            list.Add(sensor);
        }

        var roles = new Dictionary<string, SensorRole>(StringComparer.Ordinal);
        var limits = new List<LimitSensor>();

        void AddRole(SensorReading sensor, string role, Confidence confidence, int? instance = null) =>
            roles.TryAdd(sensor.Id, new SensorRole(sensor.Id, role, sensor.HardwareId, confidence, instance));

        AddRoleFn addRole = AddRole;

        var primaryCpuId = orderedHardware.FirstOrDefault(h => h.Type == "Cpu")?.Id;
        var primaryGpuId = ChoosePrimaryGpu(orderedHardware, sensorsByHardware);
        var cpuControlIsCcdMax = false;

        foreach (var hw in orderedHardware)
        {
            if (!sensorsByHardware.TryGetValue(hw.Id, out var sensors))
            {
                sensors = [];
            }

            switch (hw.Type)
            {
                case "Cpu" when hw.Id == primaryCpuId:
                    cpuControlIsCcdMax = ClassifyCpu(hw.Id, sensors, limits, addRole);
                    break;

                case "GpuNvidia" or "GpuAmd" or "GpuIntel":
                    if (hw.Id == primaryGpuId)
                    {
                        ClassifyPrimaryGpu(sensors, addRole);
                    }
                    else if (hw.Type != "GpuNvidia")
                    {
                        // A non-primary AMD/Intel GPU is treated as integrated: only the
                        // igpu.* roles apply, and only when the matching sensors exist
                        // (an AMD APU iGPU with no "GPU Core" sensors legitimately gets
                        // zero roles). A non-primary discrete NVIDIA GPU gets no roles at
                        // all (documented ambiguity: the spec has no "secondary GPU" role
                        // bucket).
                        ClassifyIntegratedGpu(sensors, addRole);
                    }

                    break;

                case "SuperIO":
                    ClassifySuperIo(sensors, addRole);
                    break;

                case "Memory":
                    ClassifyMemory(hw, sensors, addRole, limits);
                    break;

                case "Storage":
                    ClassifyStorage(hw, sensors, addRole, limits);
                    break;

                case "Battery":
                    ClassifyBattery(sensors, addRole);
                    break;
            }
        }

        var missing = new List<string>();
        if (!roles.Values.Any(r => r.Role == Roles.CpuTempControl))
        {
            missing.Add(Roles.CpuTempControl);
        }

        if (primaryGpuId is not null && !roles.Values.Any(r => r.Role == Roles.GpuTempCore))
        {
            missing.Add(Roles.GpuTempCore);
        }

        var sortedRoles = roles.Values
            .OrderBy(r => r.SensorId, StringComparer.Ordinal)
            .ThenBy(r => r.Role, StringComparer.Ordinal)
            .ToList();
        var sortedLimits = limits
            .OrderBy(l => l.SensorId, StringComparer.Ordinal)
            .ThenBy(l => l.Kind, StringComparer.Ordinal)
            .ToList();

        return new ClassificationResult(sortedRoles, sortedLimits, primaryCpuId, primaryGpuId, missing, cpuControlIsCcdMax);
    }

    // ---- Primary GPU selection -------------------------------------------------

    private static string? ChoosePrimaryGpu(
        List<HardwareInfo> orderedHardware,
        Dictionary<string, List<SensorReading>> sensorsByHardware)
    {
        // The "/gpu-intel-integrated/" identifier path is a stronger, higher-priority
        // signal than any name/value-based discrete heuristic (spec classification
        // order: Type > SensorType > Identifier > Name), so those hardware entries never
        // become primaryGpuId even via the "any GPU with a GPU Core temperature"
        // fallback below -- they always take the igpu.* path instead.
        var gpus = orderedHardware
            .Where(h => h.Type is "GpuNvidia" or "GpuAmd" or "GpuIntel")
            .Where(h => !h.Id.StartsWith("/gpu-intel-integrated/", StringComparison.Ordinal))
            .ToList();
        var nvidia = gpus.Where(h => h.Type == "GpuNvidia").ToList();
        if (nvidia.Count > 0)
        {
            return nvidia
                .OrderByDescending(h => GetSmallData(sensorsByHardware, h.Id, "gpu memory total") ?? -1f)
                .ThenBy(orderedHardware.IndexOf)
                .First()
                .Id;
        }

        var discrete = gpus.FirstOrDefault(h => IsDiscreteAmdOrIntel(sensorsByHardware, h.Id));
        if (discrete is not null)
        {
            return discrete.Id;
        }

        var anyWithCoreTemp = gpus.FirstOrDefault(h => HasSensor(sensorsByHardware, h.Id, "Temperature", "gpu core"));
        return anyWithCoreTemp?.Id;
    }

    private static bool IsDiscreteAmdOrIntel(Dictionary<string, List<SensorReading>> sensorsByHardware, string hardwareId)
    {
        if (!HasSensor(sensorsByHardware, hardwareId, "Temperature", "gpu core"))
        {
            return false;
        }

        var memoryTotal = GetSmallData(sensorsByHardware, hardwareId, "gpu memory total")
            ?? GetSmallData(sensorsByHardware, hardwareId, "d3d dedicated memory total");
        var hasHotspotOrJunction =
            HasSensor(sensorsByHardware, hardwareId, "Temperature", "gpu hot spot") ||
            HasSensor(sensorsByHardware, hardwareId, "Temperature", "gpu memory junction");

        return (memoryTotal is >= 2048f) || hasHotspotOrJunction;
    }

    private static float? GetSmallData(Dictionary<string, List<SensorReading>> sensorsByHardware, string hardwareId, string normalizedName)
    {
        if (!sensorsByHardware.TryGetValue(hardwareId, out var sensors))
        {
            return null;
        }

        return sensors.FirstOrDefault(s => s.Type == "SmallData" && Normalize(s.Name) == normalizedName)?.Value;
    }

    private static bool HasSensor(Dictionary<string, List<SensorReading>> sensorsByHardware, string hardwareId, string type, string normalizedName)
    {
        if (!sensorsByHardware.TryGetValue(hardwareId, out var sensors))
        {
            return false;
        }

        return sensors.Any(s => s.Type == type && Normalize(s.Name) == normalizedName);
    }

    // ---- Name helpers -----------------------------------------------------------

    internal static string Normalize(string name) => name.Trim().ToLowerInvariant();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();

    internal static int? ExtractInstance(string name)
    {
        var match = DigitsPattern().Match(name);
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingDigitsPattern();

    internal static int? ExtractTrailingInstance(string id)
    {
        var match = TrailingDigitsPattern().Match(id);
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }
}
