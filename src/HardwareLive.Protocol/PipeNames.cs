using System.Runtime.Versioning;
using System.Security.Principal;

namespace HardwareLive.Protocol;

public static class PipeNames
{
    [SupportedOSPlatform("windows")]
    public static string ForUser(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        return $"HardwareLive.Sampler.{sid.Value}";
    }

    /// <summary>
    /// A fresh, unique pipe name for tests. <c>SamplerClient</c> and <c>SamplerService</c>
    /// default to <see cref="ForUser"/>, but a test that constructs its own pipe server/
    /// client pair must never share that real, per-user production name: a real
    /// <c>hl-sampler.exe</c> already running on the same machine owns it, and "All pipe
    /// instances are busy" (only one server instance is ever allowed, docs/SPEC.md's
    /// single-writer pipe) fails the test for a reason that has nothing to do with what
    /// it's checking.
    /// </summary>
    public static string ForTest() => $"HardwareLive.Test.{Guid.NewGuid():N}";
}
