using HardwareLive.Core.Classification;

namespace HardwareLive.Core;

internal static class BuiltInLayouts
{
    public static readonly IReadOnlyList<Layout> All =
    [
        new(
            "builtin-overview",
            "Overview",
            [
                Analysis(), Notes(),
                Tile(Roles.CpuTempControl), Tile(Roles.CpuPowerPackage), Tile(Roles.CpuLoadTotal),
                Tile(Roles.CpuClockEffectiveAvg), Tile(Roles.FanCpu), Tile(Roles.GpuTempCore),
                Tile(Roles.GpuTempMem), Tile(Roles.GpuPower), Tile(Roles.GpuLoadCore),
                Tile(Roles.GpuClockCore), Tile(Roles.DimmTemp), Tile(Roles.StorageTemp),
                Tile(Roles.BoardTempVrm),
                Chart(Roles.CpuTempControl, Roles.GpuTempCore, Roles.GpuTempMem, Roles.BoardTempVrm),
                Chart(Roles.CpuPowerPackage, Roles.GpuPower),
                ChartWithMax(100, Roles.CpuLoadTotal, Roles.GpuLoadCore, Roles.RamLoad),
                Chart(Roles.FanCpu, Roles.FanPump, Roles.GpuFan),
            ]),
        new(
            "builtin-cpu",
            "CPU",
            [
                Analysis(), Tile(Roles.CpuTempControl), Tile(Roles.CpuTempCcd), Tile(Roles.CpuTempCore),
                Tile(Roles.CpuPowerPackage), Tile(Roles.CpuClockEffectiveAvg),
                Tile(Roles.CpuClockEffectiveCore), Tile(Roles.CpuLoadTotal), Tile(Roles.FanCpu),
                Tile(Roles.FanCpuOpt), Tile(Roles.FanPump), Tile(Roles.BoardTempVrm),
                Chart(Roles.CpuTempControl, Roles.CpuTempCcd, Roles.CpuTempCore, Roles.CpuPowerPackage),
                Chart(Roles.CpuClockEffectiveAvg, Roles.CpuClockEffectiveCore, Roles.CpuLoadTotal),
            ],
            Focus: "cpu"),
        new(
            "builtin-gpu",
            "GPU",
            [
                Analysis(), Tile(Roles.GpuTempCore), Tile(Roles.GpuTempHotspot), Tile(Roles.GpuTempMem),
                Tile(Roles.GpuPower), Tile(Roles.GpuPowerPct), Tile(Roles.GpuClockCore),
                Tile(Roles.GpuClockMem), Tile(Roles.GpuLoadCore), Tile(Roles.GpuVramUsed),
                Tile(Roles.GpuFan), Tile(Roles.GpuVoltageCore),
                Chart(Roles.GpuTempCore, Roles.GpuTempHotspot, Roles.GpuTempMem),
                Chart(Roles.GpuPower, Roles.GpuPowerPct, Roles.GpuLoadCore),
            ],
            Focus: "gpu"),
        new(
            "builtin-gaming",
            "3D Gaming",
            [
                Analysis(), Tile("fps.avg"), Tile("fps.low1"), Chart("frametime.ms"), Tile("fps.app"),
                Tile(Roles.CpuTempControl), Tile(Roles.CpuClockEffectiveAvg), Tile(Roles.CpuLoadTotal),
                Tile(Roles.GpuTempCore), Tile(Roles.GpuTempHotspot), Tile(Roles.GpuTempMem),
                Tile(Roles.GpuPower), Tile(Roles.GpuClockCore), Tile(Roles.GpuLoadCore),
                Tile(Roles.GpuVramUsed), Tile(Roles.RamUsed),
                ChartWithMax(100, Roles.CpuLoadTotal, Roles.GpuLoadCore),
            ],
            Focus: "gaming"),
        new(
            "builtin-thermals",
            "Thermals",
            [
                Tile(Roles.CpuTempControl), Tile(Roles.CpuTempCcd), Tile(Roles.CpuTempCore),
                Tile(Roles.CpuTempMax), Tile(Roles.CpuTempAvg), Tile(Roles.GpuTempCore),
                Tile(Roles.GpuTempHotspot), Tile(Roles.GpuTempMem), Tile(Roles.IgpuTempCore),
                Tile(Roles.BoardTempCpu), Tile(Roles.BoardTempVrm), Tile(Roles.BoardTempChipset),
                Tile(Roles.BoardTempPcie), Tile(Roles.BoardTempSystem), Tile(Roles.DimmTemp),
                Tile(Roles.StorageTemp), Tile(Roles.StorageTempSensor), Tile(Roles.BatteryTemp),
            ],
            Sort: "headroom"),
        new(
            "builtin-cooling",
            "Cooling",
            [
                Tile(Roles.FanCpu), Tile(Roles.FanCpuOpt), Tile(Roles.FanPump), Tile(Roles.FanSystem),
                Tile(Roles.FanDuty), Tile(Roles.GpuFan), Tile(Roles.GpuFanDuty),
                Tile(Roles.CpuTempControl), Tile(Roles.GpuTempCore), Tile(Roles.BoardTempVrm),
            ]),
        new(
            "builtin-storage",
            "Storage",
            [
                Tile(Roles.StorageTemp), Tile(Roles.StorageTempSensor),
                Tile(Roles.StorageLife), Tile(Roles.StorageSpare),
            ]),
    ];

    private static LayoutWidget Tile(string role) => new("tile", "S", new LayoutReference(role, null, null));

    private static LayoutWidget Analysis() => new("analysis", "L", new LayoutReference("analysis.health", null, null));

    private static LayoutWidget Notes() => new("notes", "L", new LayoutReference("notes", null, null));

    private static LayoutWidget Chart(params string[] roles) => new(
        "chart",
        "L",
        Ref: null,
        Series: roles.Select(role => new LayoutReference(role, null, null)).ToArray());

    private static LayoutWidget ChartWithMax(double max, params string[] roles) =>
        Chart(roles) with { Max = max };
}
