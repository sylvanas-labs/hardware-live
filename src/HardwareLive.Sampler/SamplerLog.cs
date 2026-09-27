using System.Security.AccessControl;
using System.Security.Principal;

namespace HardwareLive.Sampler;

/// <summary>Process-lifetime static facade used by production code; tests exercise
/// <see cref="SamplerFileLog"/> directly with an injected directory and clock.</summary>
public static class SamplerLog
{
    private static readonly Lazy<SamplerFileLog> Instance = new(() => new SamplerFileLog(DefaultDirectory));

    public static void Write(string message) => Instance.Value.Write(message);

    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HardwareLive",
        "logs");
}

/// <summary>
/// Writes to <c>sampler.log</c> under an admin-only ACL'd directory, refusing to log at
/// all (never throwing) if the directory can't be trusted: not elevated/SYSTEM, a
/// reparse point in the path, or an untrusted owner. Caps the file at 1 MiB with one
/// rotation, and rate-limits identical repeated messages to once per 60 seconds.
/// </summary>
public sealed class SamplerFileLog
{
    private const long MaxBytes = 1 * 1024 * 1024;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly bool _privileged;
    private readonly Func<string, bool> _isTrustedOwner;
    private readonly object _sync = new();
    private readonly Dictionary<string, RateState> _rateState = new(StringComparer.Ordinal);
    private bool _disabled;
    private bool _checked;

    public SamplerFileLog(
        string directory,
        TimeProvider? clock = null,
        bool? isPrivilegedOverride = null,
        Func<string, bool>? isTrustedOwnerOverride = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _clock = clock ?? TimeProvider.System;
        _privileged = isPrivilegedOverride ?? ComputeIsElevatedOrSystem();
        _isTrustedOwner = isTrustedOwnerOverride ?? IsAdministratorsOrSystemOwned;
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

                if (!EnsureTrustedDirectory())
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

                AppendLine(toWrite, now);
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

    private void AppendLine(string message, DateTimeOffset now)
    {
        var path = Path.Combine(_directory, "sampler.log");
        RotateIfOversize(path);
        var line = $"{now:O} {message}{Environment.NewLine}";
        File.AppendAllText(path, line);
    }

    private static void RotateIfOversize(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var rotated = path + ".1";
        File.Copy(path, rotated, overwrite: true);
        File.WriteAllText(path, string.Empty);
    }

    /// <summary>
    /// Ensures the log directory exists with a protected ACL (creating it on first use)
    /// and, if it already existed, that nothing on the path (the directory or the log
    /// file) is a reparse point and that the directory's owner is trusted. Never throws.
    /// </summary>
    private bool EnsureTrustedDirectory()
    {
        if (_checked)
        {
            return !_disabled;
        }

        _checked = true;

        try
        {
            if (Directory.Exists(_directory))
            {
                if (HasReparsePoint(_directory) || HasReparsePoint(Path.Combine(_directory, "sampler.log")))
                {
                    return false;
                }

                var owner = new DirectoryInfo(_directory).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null || !_isTrustedOwner(owner.Value))
                {
                    return false;
                }

                return true;
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null));
            AddDirectoryRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddDirectoryRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddDirectoryRule(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);

            Directory.CreateDirectory(Path.GetDirectoryName(_directory) ?? _directory);
            new DirectoryInfo(_directory).Create(security);
            return true;
        }
        catch
        {
            return false;
        }
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
}
