namespace HardwareLive.Core.Fps;

/// <summary>
/// Stable ids for the synthetic FPS hardware/sensors (docs/SPEC.md Component 9 / step7-fps
/// item 5). <see cref="FpsService"/> appends exactly these to every sampler frame so the rest
/// of the pipeline (history, classifier, presets, charts) treats them like any other sensor.
/// <see cref="AppField"/> is deliberately not a sensor id: <c>fps.app</c> is a string and is
/// exposed only as the top-level <c>fps</c> field of <c>/api/snapshot</c>.
/// </summary>
public static class FpsSensorIds
{
    public const string HardwareId = "/fps";
    public const string HardwareName = "Frame rate";
    public const string HardwareType = "Fps";

    public const string Avg = "/fps/avg";
    public const string Low1 = "/fps/low1";
    public const string FrametimeMs = "/fps/frametime";
    public const string FrametimeJitter = "/fps/jitter";

    /// <summary>Not a sensor id -- documents the reserved widget-ref role name only.</summary>
    public const string AppField = "fps.app";
}
