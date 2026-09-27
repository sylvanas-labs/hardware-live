using System.Text.RegularExpressions;
using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

public static partial class SensorClassifier
{
    [GeneratedRegex(@"^ccd\d+\s*\(tdie\)$")]
    private static partial Regex CcdTdiePattern();

    [GeneratedRegex(@"^cpu core #\d+$")]
    private static partial Regex IntelCoreTempPattern();

    [GeneratedRegex(@"^core #\d+ \(smu\)$")]
    private static partial Regex AmdCorePowerPattern();

    [GeneratedRegex(@"^core #\d+ \(effective\)$")]
    private static partial Regex AmdEffectiveClockPattern();

    [GeneratedRegex(@"^core #\d+$")]
    private static partial Regex AmdClockPattern();

    private static bool ClassifyCpu(string hardwareId, List<SensorReading> sensors, List<LimitSensor> limits, AddRoleFn addRole)
    {
        var isAmd = hardwareId.StartsWith("/amdcpu/", StringComparison.Ordinal);
        var isIntel = hardwareId.StartsWith("/intelcpu/", StringComparison.Ordinal);
        if (!isAmd && !isIntel)
        {
            // Unknown CPU vendor path: no LHM facts to classify against. Leave
            // unclassified rather than guess (documented ambiguity).
            return false;
        }

        var temps = sensors.Where(s => s.Type == "Temperature").ToList();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var cpuControlIsCcdMax = false;

        if (isAmd)
        {
            cpuControlIsCcdMax = ClassifyAmdControlTemp(temps, handled, addRole);
            foreach (var s in temps.Where(s => !handled.Contains(s.Id) && CcdTdiePattern().IsMatch(Normalize(s.Name))))
            {
                addRole(s, Roles.CpuTempCcd, Confidence.High, ExtractInstance(s.Name));
            }
        }
        else
        {
            ClassifyIntelDistanceToTjMax(temps, handled, limits);
            ClassifyIntelControlTemp(temps, handled, addRole);
            foreach (var s in temps.Where(s => !handled.Contains(s.Id)))
            {
                var name = Normalize(s.Name);
                if (name == "core max")
                {
                    addRole(s, Roles.CpuTempMax, Confidence.High);
                }
                else if (name == "core average")
                {
                    addRole(s, Roles.CpuTempAvg, Confidence.High);
                }
                else if (IntelCoreTempPattern().IsMatch(name))
                {
                    addRole(s, Roles.CpuTempCore, Confidence.High, ExtractInstance(name));
                }
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Power"))
        {
            var name = Normalize(s.Name);
            if (name is "package" or "cpu package")
            {
                addRole(s, Roles.CpuPowerPackage, Confidence.High);
            }
            else if (isAmd && AmdCorePowerPattern().IsMatch(name))
            {
                addRole(s, Roles.CpuPowerCore, Confidence.High, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Clock"))
        {
            var name = Normalize(s.Name);
            if (name == "cores (average)")
            {
                addRole(s, Roles.CpuClockAvg, Confidence.High);
            }
            else if (name == "cores (average effective)")
            {
                addRole(s, Roles.CpuClockEffectiveAvg, Confidence.High);
            }
            else if (isAmd && AmdEffectiveClockPattern().IsMatch(name))
            {
                addRole(s, Roles.CpuClockEffectiveCore, Confidence.High, ExtractInstance(name));
            }
            else if (isAmd && AmdClockPattern().IsMatch(name))
            {
                addRole(s, Roles.CpuClockCore, Confidence.High, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Load"))
        {
            var name = Normalize(s.Name);
            if (name == "cpu total")
            {
                addRole(s, Roles.CpuLoadTotal, Confidence.High);
            }
            else if (name == "cpu core max")
            {
                addRole(s, Roles.CpuLoadMax, Confidence.High);
            }
            else if (IntelCoreTempPattern().IsMatch(name)) // same shape as "cpu core #n"
            {
                addRole(s, Roles.CpuLoadCore, Confidence.High, ExtractInstance(name));
            }
        }

        foreach (var s in sensors.Where(s => s.Type == "Voltage"))
        {
            if (Normalize(s.Name) == "cpu core")
            {
                addRole(s, Roles.CpuVoltageCore, Confidence.High);
            }
        }

        return cpuControlIsCcdMax;
    }

    /// <summary>Returns true when the control temperature was resolved via the CCD
    /// fallback (no Tctl/Tdie-style sensor exists), so the caller can flag
    /// <see cref="ClassificationResult.CpuControlIsCcdMax"/>. The winning CCD still gets
    /// the sole <see cref="Roles.CpuTempControl"/> role (never also
    /// <see cref="Roles.CpuTempCcd"/>, since it's added to <paramref name="handled"/>);
    /// HealthAnalyzer is responsible for re-including it when computing max(all CCDs).</summary>
    private static bool ClassifyAmdControlTemp(List<SensorReading> temps, HashSet<string> handled, AddRoleFn addRole)
    {
        SensorReading? control =
            FindExact(temps, "core (tctl/tdie)") ??
            FindExact(temps, "core (tctl)") ??
            FindExact(temps, "core (tdie)") ??
            FindExact(temps, "tctl");
        var confidence = Confidence.High;
        var isCcdFallback = false;

        if (control is null)
        {
            var ccdCandidates = temps.Where(s => CcdTdiePattern().IsMatch(Normalize(s.Name))).ToList();
            if (ccdCandidates.Count > 0)
            {
                control = ccdCandidates.OrderByDescending(s => s.Value ?? float.NegativeInfinity).First();
                confidence = Confidence.Medium;
                isCcdFallback = true;
            }
        }

        if (control is not null)
        {
            addRole(control, Roles.CpuTempControl, confidence);
            handled.Add(control.Id);
        }

        return isCcdFallback;
    }

    private static void ClassifyIntelControlTemp(List<SensorReading> temps, HashSet<string> handled, AddRoleFn addRole)
    {
        var control = FindExact(temps.Where(s => !handled.Contains(s.Id)), "cpu package");
        var confidence = Confidence.High;

        if (control is null)
        {
            control = FindExact(temps.Where(s => !handled.Contains(s.Id)), "core max");
            confidence = Confidence.Medium;
        }

        if (control is not null)
        {
            addRole(control, Roles.CpuTempControl, confidence);
            handled.Add(control.Id);
        }
    }

    private static void ClassifyIntelDistanceToTjMax(List<SensorReading> temps, HashSet<string> handled, List<LimitSensor> limits)
    {
        foreach (var s in temps.Where(s => Normalize(s.Name).Contains("distance to tjmax")))
        {
            var instance = ExtractInstance(s.Name);
            var appliesTo = instance is int n
                ? temps.FirstOrDefault(c => Normalize(c.Name) == $"cpu core #{n}")?.Id ?? s.HardwareId
                : s.HardwareId;
            limits.Add(new LimitSensor(s.Id, appliesTo, LimitKinds.TjmaxDistance));
            handled.Add(s.Id);
        }
    }

    private static SensorReading? FindExact(IEnumerable<SensorReading> sensors, string normalizedName) =>
        sensors.FirstOrDefault(s => Normalize(s.Name) == normalizedName);
}
