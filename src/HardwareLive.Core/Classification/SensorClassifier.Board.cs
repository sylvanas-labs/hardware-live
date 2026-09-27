using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

public static partial class SensorClassifier
{
    private static void ClassifySuperIo(List<SensorReading> sensors, AddRoleFn addRole)
    {
        foreach (var s in sensors.Where(s => s.Type == "Fan"))
        {
            var name = Normalize(s.Name);
            if (name.Contains("pump"))
            {
                addRole(s, Roles.FanPump, Confidence.High, ExtractInstance(name));
            }
            else if (name == "cpu fan")
            {
                addRole(s, Roles.FanCpu, Confidence.High);
            }
            else if (name == "cpu optional fan")
            {
                // Unknowable whether this drives a pump; never inferred as fan.pump.
                addRole(s, Roles.FanCpuOpt, Confidence.High);
            }
            else if (name.StartsWith("system fan", StringComparison.Ordinal))
            {
                addRole(s, Roles.FanSystem, Confidence.High, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Control"))
        {
            // Read-only Super-I/O duty-cycle (%) sensors: classified purely by sensor
            // Type, with no name discrimination available, so this is the "type-only
            // fallback" tier per the spec's priority rule (Low confidence).
            addRole(s, Roles.FanDuty, Confidence.Low, ExtractInstance(s.Name));
        }

        foreach (var s in sensors.Where(s => s.Type == "Temperature"))
        {
            var name = Normalize(s.Name);
            if (name == "cpu")
            {
                // The Super-I/O socket temperature, NOT the CPU's own control temp.
                addRole(s, Roles.BoardTempCpu, Confidence.High);
            }
            else if (name is "pch" or "chipset")
            {
                addRole(s, Roles.BoardTempChipset, Confidence.High);
            }
            else if (name.StartsWith("system", StringComparison.Ordinal))
            {
                addRole(s, Roles.BoardTempSystem, Confidence.High, ExtractInstance(name));
            }
            else if (name.StartsWith("pcie", StringComparison.Ordinal))
            {
                addRole(s, Roles.BoardTempPcie, Confidence.Medium, ExtractInstance(name));
            }
            else if (name.Contains("vrm") || name.Contains("vsoc"))
            {
                addRole(s, Roles.BoardTempVrm, Confidence.Medium, ExtractInstance(name));
            }
        }
    }

    private static void ClassifyMemory(HardwareInfo hardware, List<SensorReading> sensors, AddRoleFn addRole, List<LimitSensor> limits)
    {
        if (hardware.Id == "/ram")
        {
            foreach (var s in sensors)
            {
                var name = Normalize(s.Name);
                if (s.Type == "Load" && name == "memory")
                {
                    addRole(s, Roles.RamLoad, Confidence.High);
                }
                else if (s.Type == "Data" && name == "memory used")
                {
                    addRole(s, Roles.RamUsed, Confidence.High);
                }
                else if (s.Type == "Data" && name == "memory available")
                {
                    addRole(s, Roles.RamAvailable, Confidence.High);
                }
            }

            return;
        }

        if (hardware.Id == "/vram")
        {
            foreach (var s in sensors.Where(s => s.Type == "Load" && Normalize(s.Name) == "memory"))
            {
                addRole(s, Roles.PagefileLoad, Confidence.High);
            }

            return;
        }

        if (!hardware.Id.StartsWith("/memory/dimm/", StringComparison.Ordinal))
        {
            return;
        }

        var temps = sensors.Where(s => s.Type == "Temperature").ToList();
        string? liveTempId = null;
        foreach (var s in temps)
        {
            var name = Normalize(s.Name);
            if (name.StartsWith("dimm #", StringComparison.Ordinal))
            {
                addRole(s, Roles.DimmTemp, Confidence.High, ExtractInstance(name));
                liveTempId = s.Id;
            }
        }

        foreach (var s in temps)
        {
            var name = Normalize(s.Name);
            var appliesTo = liveTempId ?? hardware.Id;
            switch (name)
            {
                case "thermal sensor high limit":
                    limits.Add(new LimitSensor(s.Id, appliesTo, LimitKinds.High));
                    break;
                case "thermal sensor critical high limit":
                    limits.Add(new LimitSensor(s.Id, appliesTo, LimitKinds.Critical));
                    break;
                case "thermal sensor low limit":
                case "thermal sensor critical low limit":
                    // No distinct "critical-low" kind exists in the spec's 4-value enum;
                    // both low-side limits map to "low" (documented ambiguity).
                    limits.Add(new LimitSensor(s.Id, appliesTo, LimitKinds.Low));
                    break;
                // "Temperature Sensor Resolution" is precision metadata, not a limit.
            }
        }
    }

    private static void ClassifyBattery(List<SensorReading> sensors, AddRoleFn addRole)
    {
        foreach (var s in sensors)
        {
            var name = Normalize(s.Name);
            switch (s.Type)
            {
                case "Level" when name == "charge level":
                    addRole(s, Roles.BatteryCharge, Confidence.High);
                    break;
                case "Level" when name == "degradation level":
                    addRole(s, Roles.BatteryHealth, Confidence.High);
                    break;
                case "Power" when name == "charge/discharge rate":
                    addRole(s, Roles.BatteryRate, Confidence.High);
                    break;
                case "Temperature" when name == "battery temperature":
                    addRole(s, Roles.BatteryTemp, Confidence.High);
                    break;
            }
        }
    }
}
