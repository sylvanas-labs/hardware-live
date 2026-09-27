namespace HardwareLive.Protocol;

/// <summary>
/// Validates a deserialized <see cref="SensorFrame"/> before it is trusted anywhere else
/// in the client. A sampler is a same-machine, ACL-checked peer, but it's still an
/// external process across a pipe: a corrupted or hostile frame must be dropped, never
/// crash or wedge the reader.
/// </summary>
public static class FrameValidator
{
    public const int MaxHardwareCount = 500;
    public const int MaxSensorCount = 5000;
    public const int MaxStringLength = 256;

    public static bool Validate(SensorFrame? frame)
    {
        if (frame is null)
        {
            return false;
        }

        if (frame.Version != SensorFrame.CurrentVersion)
        {
            return false;
        }

        if (frame.Hardware is null || frame.Sensors is null)
        {
            return false;
        }

        if (frame.Hardware.Count > MaxHardwareCount || frame.Sensors.Count > MaxSensorCount)
        {
            return false;
        }

        if (string.IsNullOrEmpty(frame.LhmVersion) || frame.LhmVersion.Length > MaxStringLength)
        {
            return false;
        }

        foreach (var hardware in frame.Hardware)
        {
            if (hardware is null)
            {
                return false;
            }

            if (!IsValidId(hardware.Id) || !IsValidRequiredString(hardware.Name) || !IsValidRequiredString(hardware.Type))
            {
                return false;
            }

            if (hardware.ParentId is { Length: > MaxStringLength })
            {
                return false;
            }
        }

        foreach (var sensor in frame.Sensors)
        {
            if (sensor is null)
            {
                return false;
            }

            if (!IsValidId(sensor.Id) || !IsValidId(sensor.HardwareId) ||
                !IsValidRequiredString(sensor.Name) || !IsValidRequiredString(sensor.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidId(string? value) => IsValidRequiredString(value);

    private static bool IsValidRequiredString(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxStringLength;
}
