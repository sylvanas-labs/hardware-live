using System.Text.RegularExpressions;
using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

public static partial class SensorClassifier
{
    [GeneratedRegex(@"^temperature #\d+$")]
    private static partial Regex NvmeExtraTempPattern();

    private static void ClassifyStorage(HardwareInfo hardware, List<SensorReading> sensors, AddRoleFn addRole, List<LimitSensor> limits)
    {
        var isNvme = hardware.Id.StartsWith("/nvme/", StringComparison.Ordinal);
        var driveInstance = ExtractTrailingInstance(hardware.Id);
        var temps = sensors.Where(s => s.Type == "Temperature").ToList();

        string? liveTempId = null;
        foreach (var s in temps)
        {
            var name = Normalize(s.Name);
            if ((isNvme && name == "composite temperature") || (!isNvme && name == "temperature"))
            {
                addRole(s, Roles.StorageTemp, Confidence.High, driveInstance);
                liveTempId = s.Id;
            }
        }

        foreach (var s in temps)
        {
            var name = Normalize(s.Name);
            if (isNvme && NvmeExtraTempPattern().IsMatch(name))
            {
                addRole(s, Roles.StorageTempSensor, Confidence.Medium, ExtractInstance(name));
            }
            else if (isNvme && name == "warning temperature")
            {
                limits.Add(new LimitSensor(s.Id, liveTempId ?? hardware.Id, LimitKinds.Warning));
            }
            else if (isNvme && name == "critical temperature")
            {
                limits.Add(new LimitSensor(s.Id, liveTempId ?? hardware.Id, LimitKinds.Critical));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Level"))
        {
            var name = Normalize(s.Name);
            if (name == "life")
            {
                addRole(s, Roles.StorageLife, Confidence.High, driveInstance);
            }
            else if (name == "available spare")
            {
                addRole(s, Roles.StorageSpare, Confidence.High, driveInstance);
            }

            // "Available Spare Threshold" and "Percentage Used" have no role or limit
            // kind in the spec; left unclassified (documented ambiguity).
        }
    }
}
