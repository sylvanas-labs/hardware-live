using HardwareLive.Core.Classification;

namespace HardwareLive.Core.Analysis;

/// <summary>
/// Short, plain-English labels for roles, used to build health/concern messages. Never the
/// raw hardware or sensor name: those come from vendor firmware/SPD strings and can contain
/// control characters (docs/SPEC.md: one real DIMM name has embedded CR/NUL bytes).
/// </summary>
internal static class RoleLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [Roles.CpuTempControl] = "CPU temp",
        [Roles.GpuTempCore] = "GPU core temp",
        [Roles.GpuTempHotspot] = "GPU hotspot temp",
        [Roles.GpuTempMem] = "GPU memory temp",
        [Roles.IgpuTempCore] = "iGPU temp",
        [Roles.BoardTempVrm] = "VRM temp",
        [Roles.BoardTempChipset] = "chipset temp",
        [Roles.BoardTempPcie] = "PCIe temp",
        [Roles.BoardTempSystem] = "system temp",
        [Roles.BoardTempCpu] = "CPU socket temp",
        [Roles.DimmTemp] = "RAM temp",
        [Roles.StorageTemp] = "drive temp",
        [Roles.BatteryTemp] = "battery temp",
        [Roles.FanCpu] = "CPU fan",
        [Roles.FanCpuOpt] = "CPU optional fan",
        [Roles.FanPump] = "pump",
        [Roles.FanSystem] = "case fan",
        [Roles.GpuFan] = "GPU fan",
        [Roles.CpuLoadTotal] = "CPU load",
        [Roles.GpuLoadCore] = "GPU load",
        [Roles.CpuClockEffectiveAvg] = "CPU clock",
        [Roles.GpuClockCore] = "GPU clock",
        [Roles.GpuPowerPct] = "GPU power",
    };

    public static string For(string role) => Labels.TryGetValue(role, out var label) ? label : role;
}
