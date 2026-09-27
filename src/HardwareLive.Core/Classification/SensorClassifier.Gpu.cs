using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

public static partial class SensorClassifier
{
    private static void ClassifyPrimaryGpu(List<SensorReading> sensors, AddRoleFn addRole)
    {
        foreach (var s in sensors.Where(s => s.Type == "Temperature"))
        {
            var name = Normalize(s.Name);
            if (name == "gpu core")
            {
                addRole(s, Roles.GpuTempCore, Confidence.High);
            }
            else if (name == "gpu hot spot")
            {
                addRole(s, Roles.GpuTempHotspot, Confidence.High);
            }
            else if (name is "gpu memory junction" or "gpu memory")
            {
                addRole(s, Roles.GpuTempMem, Confidence.High);
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Power"))
        {
            if (Normalize(s.Name) is "gpu package" or "package")
            {
                addRole(s, Roles.GpuPower, Confidence.High);
            }
        }

        // gpu.load.mem is single-instance. NVIDIA exposes both "GPU Memory Controller" (the
        // memory load) and "GPU Memory" (VRAM-used %), so the "GPU Memory" fallback is only for
        // GPUs without a controller sensor (AMD). Before ids were de-duplicated, NVIDIA's
        // "GPU Memory" load was hidden behind a colliding id, which masked this.
        var hasMemoryControllerLoad = sensors.Any(s =>
            s.Type == "Load" && Normalize(s.Name) == "gpu memory controller");

        foreach (var s in sensors.Where(s => s.Type == "Load"))
        {
            var name = Normalize(s.Name);
            if (name == "gpu core")
            {
                addRole(s, Roles.GpuLoadCore, Confidence.High);
            }
            else if (name == "gpu memory controller")
            {
                addRole(s, Roles.GpuLoadMem, Confidence.High);
            }
            else if (name == "gpu memory" && !hasMemoryControllerLoad)
            {
                // AMD has no "GPU Memory Controller" load sensor; "GPU Memory" (Load) is
                // its memory-controller-utilization equivalent (pattern fallback).
                addRole(s, Roles.GpuLoadMem, Confidence.Medium);
            }
            else if (name == "gpu board power")
            {
                addRole(s, Roles.GpuPowerPct, Confidence.High);
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Clock"))
        {
            var name = Normalize(s.Name);
            if (name == "gpu core")
            {
                addRole(s, Roles.GpuClockCore, Confidence.High);
            }
            else if (name == "gpu memory")
            {
                addRole(s, Roles.GpuClockMem, Confidence.High);
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Fan"))
        {
            var name = Normalize(s.Name);
            if (name.StartsWith("gpu fan", StringComparison.Ordinal))
            {
                addRole(s, Roles.GpuFan, Confidence.High, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Control"))
        {
            var name = Normalize(s.Name);
            if (name.StartsWith("gpu fan", StringComparison.Ordinal))
            {
                addRole(s, Roles.GpuFanDuty, Confidence.Medium, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "SmallData"))
        {
            var name = Normalize(s.Name);
            if (name == "gpu memory used")
            {
                addRole(s, Roles.GpuVramUsed, Confidence.High);
            }
            else if (name == "gpu memory total")
            {
                addRole(s, Roles.GpuVramTotal, Confidence.High);
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Voltage"))
        {
            if (Normalize(s.Name).Contains("core"))
            {
                addRole(s, Roles.GpuVoltageCore, Confidence.Medium);
            }
        }
    }

    private static void ClassifyIntegratedGpu(List<SensorReading> sensors, AddRoleFn addRole)
    {
        foreach (var s in sensors)
        {
            var name = Normalize(s.Name);
            switch (s.Type)
            {
                case "Temperature" when name == "gpu core":
                    addRole(s, Roles.IgpuTempCore, Confidence.High);
                    break;
                case "Power" when name is "gpu power" or "gpu package":
                    addRole(s, Roles.IgpuPower, Confidence.High);
                    break;
                case "Load" when name == "gpu core":
                    addRole(s, Roles.IgpuLoadCore, Confidence.High);
                    break;
                case "Clock" when name == "gpu core":
                    addRole(s, Roles.IgpuClockCore, Confidence.High);
                    break;
            }
        }
    }
}
