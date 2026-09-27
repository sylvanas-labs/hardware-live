using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HardwareLive.Sampler;

/// <summary>
/// Lets the unelevated app verify the sampler's image path. HardwareLive.Core's
/// ProcessImageVerifier opens the pipe server's process with
/// PROCESS_QUERY_LIMITED_INFORMATION, but a SYSTEM process's default DACL grants nothing
/// to an ordinary user token, so that open fails with ERROR_ACCESS_DENIED and the app
/// reports "sampler identity mismatch" forever. Serve mode therefore appends exactly one
/// ACE, for exactly the target user, with exactly that right (image name, exit code,
/// times; no memory, handle, or thread access), to the sampler's own process DACL before
/// it creates the pipe. The existing DACL is read and appended to, never replaced, so
/// SYSTEM/Administrators keep their access and Task Scheduler can still stop the task.
/// </summary>
public static class SamplerProcessSecurity
{
    public const int ProcessQueryLimitedInformation = 0x1000;

    public static void GrantQueryLimitedInformation(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var security = new KernelObjectSecurity(process.SafeHandle);
        security.AddAccessRule(new KernelObjectAccessRule(user, ProcessQueryLimitedInformation, AccessControlType.Allow));
        security.Save(process.SafeHandle);
    }

    /// <summary>Allow-ACE masks the current process's DACL grants to <paramref name="sid"/>.</summary>
    public static IReadOnlyList<int> GetAllowMasksFor(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var security = new KernelObjectSecurity(process.SafeHandle);
        var masks = new List<int>();
        foreach (AuthorizationRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule is KernelObjectAccessRule access && access.AccessControlType == AccessControlType.Allow && sid.Equals(access.IdentityReference))
            {
                masks.Add(access.Mask);
            }
        }

        return masks;
    }

    private sealed class KernelObjectSecurity : NativeObjectSecurity
    {
        public KernelObjectSecurity(SafeHandle handle)
            : base(isContainer: false, ResourceType.KernelObject, handle, AccessControlSections.Access)
        {
        }

        public void AddAccessRule(KernelObjectAccessRule rule) => base.AddAccessRule(rule);

        public void Save(SafeHandle handle) => Persist(handle, AccessControlSections.Access);

        public override Type AccessRightType => typeof(int);

        public override Type AccessRuleType => typeof(KernelObjectAccessRule);

        public override Type AuditRuleType => typeof(KernelObjectAuditRule);

        public override AccessRule AccessRuleFactory(
            IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new KernelObjectAccessRule(identityReference, accessMask, type);

        public override AuditRule AuditRuleFactory(
            IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new KernelObjectAuditRule(identityReference, accessMask, flags);
    }

    private sealed class KernelObjectAccessRule : AccessRule
    {
        public KernelObjectAccessRule(IdentityReference identity, int accessMask, AccessControlType type)
            : base(identity, accessMask, isInherited: false, InheritanceFlags.None, PropagationFlags.None, type)
        {
        }

        public int Mask => AccessMask;
    }

    private sealed class KernelObjectAuditRule : AuditRule
    {
        public KernelObjectAuditRule(IdentityReference identity, int accessMask, AuditFlags flags)
            : base(identity, accessMask, isInherited: false, InheritanceFlags.None, PropagationFlags.None, flags)
        {
        }
    }
}
