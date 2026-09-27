using HardwareLive.Core.Classification;

namespace HardwareLive.Core.Analysis;

/// <summary>
/// Short, plain-English titles for every role, used to build health/concern messages and the
/// <c>/api/meta</c> "labels" map (docs/SPEC.md Component 3 / step5-polish "Ambiguous labels").
/// Never the raw hardware or sensor name: those come from vendor firmware/SPD strings and can
/// contain control characters (docs/SPEC.md: one real DIMM name has embedded CR/NUL bytes).
/// Covers every constant in <see cref="Roles"/> -- a missing entry here is what produced the
/// "PACKAGE"/"GPU CORE"/"COMPOSITE TEMPERATURE" raw-name tiles this table replaces.
/// </summary>
internal static class RoleLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [Roles.CpuTempControl] = "CPU temperature",
        [Roles.CpuTempCcd] = "CPU CCD temperature",
        [Roles.CpuTempCore] = "CPU core temperature",
        [Roles.CpuTempMax] = "CPU max core temperature",
        [Roles.CpuTempAvg] = "CPU average core temperature",
        [Roles.CpuPowerPackage] = "CPU package power",
        [Roles.CpuPowerCore] = "CPU core power",
        [Roles.CpuClockCore] = "CPU core clock",
        [Roles.CpuClockEffectiveCore] = "CPU core clock (effective)",
        [Roles.CpuClockEffectiveAvg] = "CPU clock (effective)",
        [Roles.CpuClockAvg] = "CPU clock (average)",
        [Roles.CpuLoadTotal] = "CPU load",
        [Roles.CpuLoadMax] = "CPU max core load",
        [Roles.CpuLoadCore] = "CPU core load",
        [Roles.CpuVoltageCore] = "CPU core voltage",

        [Roles.GpuTempCore] = "GPU temperature",
        [Roles.GpuTempHotspot] = "GPU hotspot temperature",
        [Roles.GpuTempMem] = "GPU memory temperature",
        [Roles.GpuPower] = "GPU power",
        [Roles.GpuPowerPct] = "GPU power limit",
        [Roles.GpuLoadCore] = "GPU load",
        [Roles.GpuLoadMem] = "GPU memory controller load",
        [Roles.GpuClockCore] = "GPU clock",
        [Roles.GpuClockMem] = "GPU memory clock",
        [Roles.GpuFan] = "GPU fan",
        [Roles.GpuFanDuty] = "GPU fan duty",
        [Roles.GpuVramUsed] = "GPU memory used",
        [Roles.GpuVramTotal] = "GPU memory total",
        [Roles.GpuVoltageCore] = "GPU core voltage",

        [Roles.IgpuTempCore] = "iGPU temperature",
        [Roles.IgpuPower] = "iGPU power",
        [Roles.IgpuLoadCore] = "iGPU load",
        [Roles.IgpuClockCore] = "iGPU clock",

        [Roles.FanCpu] = "CPU fan",
        [Roles.FanCpuOpt] = "CPU optional fan",
        [Roles.FanPump] = "Pump",
        [Roles.FanSystem] = "System fan",
        [Roles.FanDuty] = "Fan duty",

        [Roles.BoardTempCpu] = "CPU socket temperature",
        [Roles.BoardTempVrm] = "VRM temperature",
        [Roles.BoardTempChipset] = "Chipset temperature",
        [Roles.BoardTempPcie] = "PCIe temperature",
        [Roles.BoardTempSystem] = "System temperature",

        [Roles.DimmTemp] = "RAM temperature",
        [Roles.RamLoad] = "RAM load",
        [Roles.RamUsed] = "RAM used",
        [Roles.RamAvailable] = "RAM available",
        [Roles.PagefileLoad] = "Pagefile load",

        [Roles.StorageTemp] = "Drive temperature",
        [Roles.StorageTempSensor] = "Drive temperature sensor",
        [Roles.StorageLife] = "Drive life",
        [Roles.StorageSpare] = "Drive available spare",

        [Roles.BatteryCharge] = "Battery charge",
        [Roles.BatteryRate] = "Battery charge rate",
        [Roles.BatteryTemp] = "Battery temperature",
        [Roles.BatteryHealth] = "Battery health",

        [Roles.FpsAvg] = "Average FPS",
        [Roles.FpsLow1] = "1% low FPS",
        [Roles.FrametimeMs] = "Frame time",
        [Roles.FrametimeJitter] = "Frame time jitter",
    };

    /// <summary>The degree sign is written out as an escape so it survives regardless of the
    /// source file's saved encoding (docs/SPEC.md step5-polish "Units").</summary>
    public const string DegreeCelsius = "°C";

    public static string For(string role) => Labels.TryGetValue(role, out var label) ? label : role;
}
