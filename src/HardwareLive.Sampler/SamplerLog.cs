using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HardwareLive.Sampler;

/// <summary>Process-lifetime static facade used by production code; tests exercise
/// <see cref="SamplerFileLog"/> directly with an injected directory and clock.</summary>
public static class SamplerLog
{
    private static readonly Lazy<SamplerFileLog> Instance = new(() => new SamplerFileLog(DefaultDirectory, parentDirectory: DefaultParentDirectory));

    public static void Write(string message) => Instance.Value.Write(message);

    private static string DefaultParentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HardwareLive");

    private static string DefaultDirectory => Path.Combine(DefaultParentDirectory, "logs");
}

/// <summary>
/// Writes to <c>sampler.log</c> under an admin-only ACL'd directory, refusing to log at
/// all (never throwing) if the directory tree can't be trusted: not elevated/SYSTEM, a
/// reparse point anywhere on the path, an untrusted owner, or a DACL that grants a
/// write-capable right to anyone other than SYSTEM/Administrators/CREATOR OWNER (when the
/// owner is an admin). Both <c>%ProgramData%\HardwareLive</c> (<see cref="_parentDirectory"/>,
/// when supplied) and <c>%ProgramData%\HardwareLive\logs</c> (<see cref="_directory"/>) are
/// validated: the parent is just as attacker-reachable as the logs directory itself (a
/// plain <c>Directory.CreateDirectory</c> on the parent would have inherited ProgramData's
/// permissive default DACL, letting a local user pre-seed it before the sampler ever runs
/// elevated). Caps the file at 1 MiB with one rotation, and rate-limits identical repeated
/// messages to once per 60 seconds. Holds one open <see cref="FileStream"/> for the process
/// lifetime instead of reopening the path on every write.
/// </summary>
public sealed class SamplerFileLog : IDisposable
{
    private const long MaxBytes = 1 * 1024 * 1024;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

    // Deliberately excludes FileSystemRights.FullControl itself: as a raw bitmask,
    // FullControl's value is the union of *every* right including harmless ones
    // (ReadData, ReadAndExecute, Synchronize, ...), so OR-ing it in here would flag our
    // own legitimate "Users: ReadAndExecute" grant as dangerous (ReadAndExecute's bits
    // are a subset of FullControl's). An ACE that actually grants FullControl still
    // trips this mask correctly, since FullControl's value already contains every one of
    // these specific write-capable bits too.
    private static readonly FileSystemRights DangerousRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    // Raw generic-right bits (GENERIC_WRITE / GENERIC_ALL). .NET's FileSystemAccessRule
    // API never lets you *construct* an ACE carrying these (only the OS-resolved specific
    // rights), but GetAccessRules() surfaces whatever raw access mask an ACE actually has,
    // and a maliciously crafted ACL (set outside .NET) could still carry them.
    private const int GenericWriteBit = unchecked((int)0x40000000);
    private const int GenericAllBit = unchecked((int)0x10000000);
    private static readonly int DangerousRightsMask = (int)DangerousRights | GenericWriteBit | GenericAllBit;

    private readonly string _directory;
    private readonly string? _parentDirectory;
    private readonly TimeProvider _clock;
    private readonly bool _privileged;
    private readonly Func<string, bool> _isTrustedOwner;
    private readonly Func<string, bool> _isTrustedDacl;
    private readonly object _sync = new();
    private readonly Dictionary<string, RateState> _rateState = new(StringComparer.Ordinal);
    private FileStream? _stream;
    private bool _disabled;
    private bool _checked;
    private bool _trusted;

    public SamplerFileLog(
        string directory,
        TimeProvider? clock = null,
        bool? isPrivilegedOverride = null,
        Func<string, bool>? isTrustedOwnerOverride = null,
        Func<string, bool>? isTrustedDaclOverride = null,
        string? parentDirectory = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _parentDirectory = parentDirectory;
        _clock = clock ?? TimeProvider.System;
        _privileged = isPrivilegedOverride ?? ComputeIsElevatedOrSystem();
        _isTrustedOwner = isTrustedOwnerOverride ?? IsAdministratorsOrSystemOwned;
        _isTrustedDacl = isTrustedDaclOverride ?? IsDaclSafe;
    }

    public void Write(string message)
    {
        try
        {
            lock (_sync)
            {
                if (_disabled)
                {
                    return;
                }

                if (!_privileged)
                {
                    // A manual, unelevated run (e.g. --dump) never gets file logging: it
                    // can't be trusted to create or maintain the protected ProgramData
                    // directory, and silently falling back to a user-writable location
                    // (%LOCALAPPDATA%) would defeat the whole point of the ACL.
                    _disabled = true;
                    return;
                }

                if (!EnsureTrustedDirectoryCached())
                {
                    _disabled = true;
                    return;
                }

                if (_stream is null && !TryOpenLogStream())
                {
                    _disabled = true;
                    return;
                }

                var now = _clock.GetUtcNow();
                var toWrite = ApplyRateLimit(message, now);
                if (toWrite is null)
                {
                    return;
                }

                if (!AppendLine(toWrite, now))
                {
                    _disabled = true;
                }
            }
        }
        catch
        {
            // Logging must never take down the sampler.
            lock (_sync)
            {
                _disabled = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private string? ApplyRateLimit(string message, DateTimeOffset now)
    {
        if (_rateState.TryGetValue(message, out var state))
        {
            if (now - state.LastLogged < RateLimitWindow)
            {
                _rateState[message] = state with { Suppressed = state.Suppressed + 1 };
                return null;
            }

            var suppressed = state.Suppressed;
            _rateState[message] = new RateState(now, 0);
            return suppressed > 0 ? $"{message} (repeated {suppressed} times)" : message;
        }

        _rateState[message] = new RateState(now, 0);
        return message;
    }

    // ---- Writing, over one held-open handle -------------------------------------------

    private bool AppendLine(string message, DateTimeOffset now)
    {
        var bytes = Encoding.UTF8.GetBytes($"{now:O} {message}{Environment.NewLine}");

        if (_stream!.Length + bytes.Length > MaxBytes && !Rotate())
        {
            return false;
        }

        _stream!.Write(bytes, 0, bytes.Length);
        _stream.Flush();
        return true;
    }

    /// <summary>Closes the held handle, renames <c>sampler.log</c> to <c>sampler.log.1</c>
    /// (overwriting any previous rotation), revalidates the directory tree, and reopens a
    /// fresh handle. Accepted residual: the rename is a plain path-based
    /// <see cref="File.Move(string, string, bool)"/>, not a handle-relative rename via a
    /// raw <c>NtSetInformationFile</c> call, so there's a narrow TOCTOU window between
    /// closing the old handle and the rename completing. Closing the handle first (rather
    /// than renaming a still-open file) is itself required on Windows, since our handle
    /// wasn't opened with <see cref="FileShare.Delete"/>.</summary>
    private bool Rotate()
    {
        _stream?.Dispose();
        _stream = null;

        try
        {
            if (!ValidateTrustUncached())
            {
                return false;
            }

            var path = Path.Combine(_directory, "sampler.log");
            var rotated = path + ".1";
            File.Move(path, rotated, overwrite: true);

            if (!ValidateTrustUncached())
            {
                return false;
            }

            return TryOpenLogStream();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens (or creates) <c>sampler.log</c> once and keeps the handle for every
    /// subsequent write. Refuses a pre-existing reparse point (checked before the open,
    /// via file attributes) or a hard-linked file (checked after the open, via
    /// <see cref="GetFileInformationByHandle"/>'s link count -- a hard link can't be
    /// detected from attributes alone, since it isn't a reparse point).</summary>
    private bool TryOpenLogStream()
    {
        var path = Path.Combine(_directory, "sampler.log");
        try
        {
            if (HasReparsePoint(path))
            {
                return false;
            }

            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);

            if (!HasSingleLink(stream))
            {
                stream.Dispose();
                return false;
            }

            stream.Seek(0, SeekOrigin.End);
            _stream = stream;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSingleLink(FileStream stream)
    {
        try
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                return false; // Fail closed: can't verify, don't trust it.
            }

            return info.NumberOfLinks <= 1;
        }
        catch
        {
            return false;
        }
    }

    // ---- Directory-tree trust: reparse point, owner, DACL ------------------------------

    private bool EnsureTrustedDirectoryCached()
    {
        if (_checked)
        {
            return _trusted;
        }

        _checked = true;
        _trusted = ValidateTrustUncached();
        return _trusted;
    }

    /// <summary>Validates (and creates with a protected ACL, if missing) both
    /// <see cref="_parentDirectory"/> (when supplied -- production always supplies it;
    /// some tests intentionally omit it to exercise a single-level directory) and
    /// <see cref="_directory"/> itself. Never throws.</summary>
    private bool ValidateTrustUncached()
    {
        try
        {
            if (_parentDirectory is not null && !EnsureTrustedPath(_parentDirectory))
            {
                return false;
            }

            return EnsureTrustedPath(_directory);
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureTrustedPath(string path)
    {
        if (Directory.Exists(path))
        {
            if (HasReparsePoint(path))
            {
                return false;
            }

            var owner = new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !_isTrustedOwner(owner.Value))
            {
                return false;
            }

            return _isTrustedDacl(path);
        }

        try
        {
            // The immediate parent must exist before DirectoryInfo.Create(security) can
            // create the leaf; this mirrors the pre-existing (unprotected) ensure-parent
            // behavior for whichever ancestor isn't itself being hardened here (e.g. the
            // real %ProgramData% root, or a flat test directory's temp-folder parent).
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            new DirectoryInfo(path).Create(BuildProtectedDirectorySecurity());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DirectorySecurity BuildProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
        AddDirectoryRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        AddDirectoryRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        AddDirectoryRule(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        return security;
    }

    private static void AddDirectoryRule(DirectorySecurity security, WellKnownSidType sidType, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidType, domainSid: null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static bool HasReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true; // Fail closed: an unreadable path is treated as untrusted.
        }
    }

    private static bool IsAdministratorsOrSystemOwned(string ownerSidValue)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        return string.Equals(ownerSidValue, administrators.Value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ownerSidValue, localSystem.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates every effective (explicit + inherited) Allow ACE on <paramref name="path"/>
    /// and fails if any dangerous right (anything that lets a principal write, append,
    /// delete, or change permissions/ownership) is granted to anyone other than SYSTEM,
    /// BUILTIN\Administrators, or CREATOR OWNER when the directory's own owner is an admin
    /// (i.e. "whoever created it, if that was an admin, may still manage it"). Deny ACEs
    /// aren't specially handled: an Allow ACE for a disallowed principal is unsafe on its
    /// own merits regardless of any Deny elsewhere, and evaluating full Allow/Deny ACE
    /// ordering semantics is out of scope here.
    /// </summary>
    private static bool IsDaclSafe(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();

            var ownerRef = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var ownerIsAdmin = ownerRef is not null && IsAdministratorsOrSystemOwned(ownerRef.Value);

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null).Value,
            };
            if (ownerIsAdmin)
            {
                allowed.Add(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, domainSid: null).Value);
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                if (((int)rule.FileSystemRights & DangerousRightsMask) == 0)
                {
                    continue;
                }

                var sid = (rule.IdentityReference as SecurityIdentifier)?.Value;
                if (sid is null || !allowed.Contains(sid))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ComputeIsElevatedOrSystem()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem)
            {
                return true;
            }

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct RateState(DateTimeOffset LastLogged, int Suppressed);

    // ---- Win32 interop: hard-link detection ---------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
