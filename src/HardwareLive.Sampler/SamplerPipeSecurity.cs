using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HardwareLive.Sampler;

public static class SamplerPipeSecurity
{
    public static PipeSecurity Create(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // The pipe's owner is BUILTIN\Administrators, not the sampler's own (SYSTEM)
        // identity: HardwareLive.Core.SamplerClient rejects any pipe whose owner isn't
        // Administrators or LocalSystem, so a non-admin local user can never spoof the
        // sampler with their own pipe of the same name.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
