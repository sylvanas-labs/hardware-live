using System.Security.Principal;

namespace HardwareLive.Tests;

/// <summary>
/// A fact that needs the BUILTIN\Administrators group in the token: the production sampler
/// pipe sets its owner to Administrators (<c>PipeSecurity.SetOwner</c>), which Windows only
/// allows from an administrator token (ERROR_INVALID_OWNER, 1307, otherwise). Without that
/// membership the test is reported as skipped with this reason instead of failing, so an
/// unelevated CI runner stays green while an elevated dev run still covers it.
/// </summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!IsAdministrator())
        {
            Skip = "Requires an administrator token (production pipe owner is BUILTIN\\Administrators).";
        }
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
