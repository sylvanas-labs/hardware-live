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
}
