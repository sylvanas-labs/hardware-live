using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HardwareLive.Core;

public interface IProcessImageVerifier
{
    bool IsExpectedServer(NamedPipeClientStream pipe, string expectedImagePath);

    /// <summary>
    /// True when the pipe's owner is BUILTIN\Administrators or LocalSystem. The kernel
    /// enforces who may set a security descriptor's owner, so a non-admin local user
    /// running their own copy of the sampler can never make this true for their pipe.
    /// </summary>
    bool HasExpectedOwner(NamedPipeClientStream pipe);

    /// <summary><see cref="IsExpectedServer"/> plus a short reason on failure, so "could not
    /// open the sampler process" and "sampler runs from the wrong path" stay distinguishable.</summary>
    bool TryVerifyServer(NamedPipeClientStream pipe, string expectedImagePath, out string? failure)
    {
        var ok = IsExpectedServer(pipe, expectedImagePath);
        failure = ok ? null : "sampler process image check failed";
        return ok;
    }
}

public sealed class ProcessImageVerifier : IProcessImageVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public bool HasExpectedOwner(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        try
        {
            var security = pipe.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            {
                return false;
            }

            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);
            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
            return owner == administrators || owner == localSystem;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public bool IsExpectedServer(NamedPipeClientStream pipe, string expectedImagePath) =>
        TryVerifyServer(pipe, expectedImagePath, out _);

    public bool TryVerifyServer(NamedPipeClientStream pipe, string expectedImagePath, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedImagePath);

        try
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            {
                failure = $"could not get the pipe server's process id (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
            if (process.IsInvalid)
            {
                // Error 5 here means the sampler didn't grant this user
                // PROCESS_QUERY_LIMITED_INFORMATION (HardwareLive.Sampler.SamplerProcessSecurity).
                failure = $"could not open the sampler process (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            var capacity = 32768u;
            var path = new StringBuilder((int)capacity);
            if (!QueryFullProcessImageName(process, flags: 0, path, ref capacity))
            {
                failure = $"could not read the sampler's image path (error {Marshal.GetLastWin32Error()})";
                return false;
            }

            var actual = Path.GetFullPath(path.ToString());
            var expected = Path.GetFullPath(expectedImagePath);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                failure = "the pipe server is not the installed sampler";
                return false;
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            failure = $"sampler process check failed ({exception.GetType().Name})";
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder imagePath,
        ref uint size);
}
