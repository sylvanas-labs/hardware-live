using System.Security.Principal;
using HardwareLive.Protocol;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace HardwareLive.Sampler;

public interface ISensorFrameSampler : IDisposable
{
    SensorFrame Sample();
}

public sealed class HardwareSensorSampler : ISensorFrameSampler
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();
    private long _sequence;
    private bool _disposed;

    public HardwareSensorSampler()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsPsuEnabled = true,
            IsBatteryEnabled = true,
            IsNetworkEnabled = false,
        };
        _computer.Open();
    }

    public SensorFrame Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _computer.Accept(_updateVisitor);

        var hardware = new List<HardwareInfo>();
        var sensors = new List<SensorReading>();
        var hardwareIds = new UniqueIds();
        var sensorIds = new UniqueIds();
        foreach (var item in _computer.Hardware)
        {
            Flatten(item, parentId: null, hardware, sensors, hardwareIds, sensorIds);
        }

        return new SensorFrame(
            SensorFrame.CurrentVersion,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsPawnIoInstalled(),
            IsElevated(),
            typeof(Computer).Assembly.GetName().Version?.ToString() ?? "unknown",
            hardware,
            sensors);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _computer.Close();
        _disposed = true;
    }

    private static void Flatten(
        IHardware item,
        string? parentId,
        ICollection<HardwareInfo> hardware,
        ICollection<SensorReading> sensors,
        UniqueIds hardwareIds,
        UniqueIds sensorIds)
    {
        var hardwareId = hardwareIds.MakeUnique(item.Identifier.ToString());
        hardware.Add(new HardwareInfo(hardwareId, item.Name, item.HardwareType.ToString(), parentId));

        foreach (var sensor in item.Sensors)
        {
            sensors.Add(new SensorReading(
                sensorIds.MakeUnique(sensor.Identifier.ToString()),
                hardwareId,
                sensor.Name,
                sensor.SensorType.ToString(),
                FiniteOrNull(sensor.Value),
                FiniteOrNull(sensor.Min),
                FiniteOrNull(sensor.Max)));
        }

        foreach (var child in item.SubHardware)
        {
            Flatten(child, hardwareId, hardware, sensors, hardwareIds, sensorIds);
        }
    }

    private static float? FiniteOrNull(float? value) =>
        value is not null && float.IsFinite(value.Value) ? value : null;

    private static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO", writable: false);
        return key is not null;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
