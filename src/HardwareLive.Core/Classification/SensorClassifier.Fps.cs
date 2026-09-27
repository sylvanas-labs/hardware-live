using HardwareLive.Protocol;

namespace HardwareLive.Core.Classification;

public static partial class SensorClassifier
{
    /// <summary>
    /// Classifies the four synthetic <c>/fps/*</c> sensors <see cref="Fps.FpsService"/> appends
    /// to every frame (docs/SPEC.md Component 9 / step7-fps item 5). These sensors are entirely
    /// ours -- their ids are stable and never come from LHM -- so, unlike every other classifier
    /// partial, this dispatches on the exact id suffix rather than name/type heuristics.
    /// </summary>
    private static void ClassifyFps(List<SensorReading> sensors, AddRoleFn addRole)
    {
        foreach (var sensor in sensors)
        {
            var role = sensor.Id switch
            {
                Fps.FpsSensorIds.Avg => Roles.FpsAvg,
                Fps.FpsSensorIds.Low1 => Roles.FpsLow1,
                Fps.FpsSensorIds.FrametimeMs => Roles.FrametimeMs,
                Fps.FpsSensorIds.FrametimeJitter => Roles.FrametimeJitter,
                _ => null,
            };

            if (role is not null)
            {
                addRole(sensor, role, Confidence.High);
            }
        }
    }
}
