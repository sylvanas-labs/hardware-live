namespace HardwareLive.Core.Classification;

/// <summary>
/// Exact role key strings, per docs/SPEC.md Component 3 / the classifier acceptance criteria.
/// Keys marked "multi" in the spec may be assigned to more than one sensor id at once.
/// </summary>
public static class Roles
{
    public const string CpuTempControl = "cpu.temp.control";
    public const string CpuTempCcd = "cpu.temp.ccd";
    public const string CpuTempCore = "cpu.temp.core";
    public const string CpuTempMax = "cpu.temp.max";
    public const string CpuTempAvg = "cpu.temp.avg";
    public const string CpuPowerPackage = "cpu.power.package";
    public const string CpuPowerCore = "cpu.power.core";
    public const string CpuClockCore = "cpu.clock.core";
    public const string CpuClockEffectiveCore = "cpu.clock.effective.core";
    public const string CpuClockEffectiveAvg = "cpu.clock.effective.avg";
    public const string CpuClockAvg = "cpu.clock.avg";
    public const string CpuLoadTotal = "cpu.load.total";
    public const string CpuLoadMax = "cpu.load.max";
    public const string CpuLoadCore = "cpu.load.core";
    public const string CpuVoltageCore = "cpu.voltage.core";

    public const string GpuTempCore = "gpu.temp.core";
    public const string GpuTempHotspot = "gpu.temp.hotspot";
    public const string GpuTempMem = "gpu.temp.mem";
    public const string GpuPower = "gpu.power";
    public const string GpuPowerPct = "gpu.power.pct";
    public const string GpuLoadCore = "gpu.load.core";
    public const string GpuLoadMem = "gpu.load.mem";
    public const string GpuClockCore = "gpu.clock.core";
    public const string GpuClockMem = "gpu.clock.mem";
    public const string GpuFan = "gpu.fan";
    public const string GpuFanDuty = "gpu.fan.duty";
    public const string GpuVramUsed = "gpu.vram.used";
    public const string GpuVramTotal = "gpu.vram.total";
    public const string GpuVoltageCore = "gpu.voltage.core";

    public const string IgpuTempCore = "igpu.temp.core";
    public const string IgpuPower = "igpu.power";
    public const string IgpuLoadCore = "igpu.load.core";
    public const string IgpuClockCore = "igpu.clock.core";

    public const string FanCpu = "fan.cpu";
    public const string FanCpuOpt = "fan.cpu.opt";
    public const string FanPump = "fan.pump";
    public const string FanSystem = "fan.system";
    public const string FanDuty = "fan.duty";

    public const string BoardTempCpu = "board.temp.cpu";
    public const string BoardTempVrm = "board.temp.vrm";
    public const string BoardTempChipset = "board.temp.chipset";
    public const string BoardTempPcie = "board.temp.pcie";
    public const string BoardTempSystem = "board.temp.system";

    public const string DimmTemp = "dimm.temp";
    public const string RamLoad = "ram.load";
    public const string RamUsed = "ram.used";
    public const string RamAvailable = "ram.available";
    public const string PagefileLoad = "pagefile.load";

    public const string StorageTemp = "storage.temp";
    public const string StorageTempSensor = "storage.temp.sensor";
    public const string StorageLife = "storage.life";
    public const string StorageSpare = "storage.spare";

    public const string BatteryCharge = "battery.charge";
    public const string BatteryRate = "battery.rate";
    public const string BatteryTemp = "battery.temp";
    public const string BatteryHealth = "battery.health";
}

/// <summary>Exact <see cref="LimitSensor"/> kind strings.</summary>
public static class LimitKinds
{
    public const string Warning = "warning";
    public const string Critical = "critical";
    public const string High = "high";
    public const string Low = "low";

    /// <summary>Intel "... Distance to TjMax" sensors: never a role, always this kind.</summary>
    public const string TjmaxDistance = "tjmaxDistance";
}
