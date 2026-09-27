using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HardwareLive.Sampler;

namespace HardwareLive.Tests;

public sealed class SamplerFileLogTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(Path.GetTempPath(), $"hl-log-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDirectory))
            {
                Directory.Delete(_baseDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void UnprivilegedProcessNeverWritesAFile()
    {
        using var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: false);

        log.Write("should never land on disk");

        Assert.False(Directory.Exists(_baseDirectory));
    }

    [AdminFact]
    public void PrivilegedProcessCreatesTheDirectoryAndWritesTheMessage()
    {
        using var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: true);

        log.Write("hello sampler");

        var logPath = Path.Combine(_baseDirectory, "sampler.log");
        Assert.True(File.Exists(logPath));
        Assert.Contains("hello sampler", ReadLogText(logPath), StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeLogRotatesToDotOneAndStartsFresh()
    {
        Directory.CreateDirectory(_baseDirectory);
        var logPath = Path.Combine(_baseDirectory, "sampler.log");
        File.WriteAllText(logPath, new string('a', 1024 * 1024 + 1));

        // The pre-existing directory wasn't created with our production ACL (it's a plain
        // temp dir owned by the current test process, with whatever DACL the OS gave the
        // user's temp folder), so bypass the owner/DACL checks here; the point of this
        // test is the size-based rotation, covered separately from the trust checks.
        using var log = new SamplerFileLog(
            _baseDirectory,
            isPrivilegedOverride: true,
            isTrustedOwnerOverride: _ => true,
            isTrustedDaclOverride: _ => true);

        log.Write("fresh line after rotation");

        var rotatedPath = logPath + ".1";
        Assert.True(File.Exists(rotatedPath));
        Assert.True(new FileInfo(rotatedPath).Length > 1024 * 1024);
        var freshContent = ReadLogText(logPath);
        Assert.Contains("fresh line after rotation", freshContent, StringComparison.Ordinal);
        Assert.True(new FileInfo(logPath).Length < 1024 * 1024);
    }

    [AdminFact]
    public void IdenticalMessagesAreRateLimitedToOncePerWindowThenReportRepeatCount()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        using var log = new SamplerFileLog(
            _baseDirectory,
            clock,
            isPrivilegedOverride: true,
            isTrustedOwnerOverride: _ => true,
            isTrustedDaclOverride: _ => true);
        var logPath = Path.Combine(_baseDirectory, "sampler.log");

        log.Write("duplicate message");
        log.Write("duplicate message");
        log.Write("duplicate message");

        var linesAfterBurst = ReadLogLines(logPath);
        Assert.Single(linesAfterBurst);

        clock.Advance(TimeSpan.FromMilliseconds(59_999));
        log.Write("duplicate message"); // still within the 60s window
        Assert.Single(ReadLogLines(logPath));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        log.Write("duplicate message"); // now past the window
        var linesAfterWindow = ReadLogLines(logPath);

        Assert.Equal(2, linesAfterWindow.Length);
        Assert.Contains("(repeated 3 times)", linesAfterWindow[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReparsePointDirectoryRefusesLoggingWithoutThrowing()
    {
        var realTarget = Path.Combine(_baseDirectory, "real-logs");
        Directory.CreateDirectory(realTarget);
        var reparsedLogs = Path.Combine(_baseDirectory, "logs");
        Directory.CreateSymbolicLink(reparsedLogs, realTarget);

        using var log = new SamplerFileLog(reparsedLogs, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(reparsedLogs, "sampler.log")));
        Assert.False(File.Exists(Path.Combine(realTarget, "sampler.log")));
    }

    [Fact]
    public void UntrustedDirectoryOwnerRefusesLoggingWithoutThrowing()
    {
        // A plain temp directory the test process created is owned by the current user,
        // not BUILTIN\Administrators or LocalSystem, so the real (non-overridden) owner
        // check must refuse it even though the process itself is "privileged".
        Directory.CreateDirectory(_baseDirectory);
        using var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(_baseDirectory, "sampler.log")));
    }

    // ---- Finding 2: directory-tree DACL, hard-link and parent-directory hardening ------

    [Fact]
    public void PermissiveDaclOnLogsDirectoryDisablesLoggingWithoutThrowing()
    {
        // Mirrors the real two-level layout (%ProgramData%\HardwareLive\logs) so the DACL
        // check runs against the directory that would actually get validated in
        // production. Owner is overridden (the test process owns what it creates, not
        // Administrators/SYSTEM) so only the DACL check is under test here.
        var parent = Path.Combine(_baseDirectory, "HardwareLive");
        var logs = Path.Combine(parent, "logs");
        Directory.CreateDirectory(logs);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(logs).SetAccessControl(security);

        using var log = new SamplerFileLog(logs, parentDirectory: parent, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(logs, "sampler.log")));
    }

    [Fact]
    public void PermissiveDaclOnParentDirectoryDisablesLoggingEvenWhenLogsDirIsClean()
    {
        // The parent (%ProgramData%\HardwareLive) is just as attacker-reachable as the
        // logs subdirectory: a permissive parent DACL must refuse logging even though the
        // logs directory itself, taken alone, would be fine.
        var parent = Path.Combine(_baseDirectory, "HardwareLive");
        var logs = Path.Combine(parent, "logs");
        Directory.CreateDirectory(logs);

        var parentSecurity = new DirectorySecurity();
        parentSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        parentSecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, domainSid: null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(parentSecurity);

        using var log = new SamplerFileLog(logs, parentDirectory: parent, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(logs, "sampler.log")));
    }

    [Fact]
    public void HardLinkedLogFileDisablesLoggingWithoutThrowing()
    {
        Directory.CreateDirectory(_baseDirectory);
        var logPath = Path.Combine(_baseDirectory, "sampler.log");
        File.WriteAllText(logPath, "seed");
        var linkPath = Path.Combine(_baseDirectory, "sampler-link.log");

        if (!CreateHardLink(linkPath, logPath, IntPtr.Zero))
        {
            // NTFS same-volume hard links should always succeed for a user-writable temp
            // directory; if the OS/filesystem still refuses (e.g. a non-NTFS temp volume),
            // there's nothing this test can exercise.
            return;
        }

        using var log = new SamplerFileLog(
            _baseDirectory,
            isPrivilegedOverride: true,
            isTrustedOwnerOverride: _ => true,
            isTrustedDaclOverride: _ => true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.DoesNotContain("must not be written", ReadLogText(logPath), StringComparison.Ordinal);
    }

    [AdminFact]
    public void HappyPathStillWritesAndRotatesUnderTheRealProductionAcl()
    {
        // End-to-end with no overrides at all: the two-level layout is created fresh with
        // the real protected ACL (owner Administrators, SYSTEM/Administrators full
        // control, Users read-only), and logging must both write and rotate through it.
        var parent = Path.Combine(_baseDirectory, "HardwareLive");
        var logs = Path.Combine(parent, "logs");
        using var log = new SamplerFileLog(logs, parentDirectory: parent, isPrivilegedOverride: true);

        // Two distinct ~600 KiB messages (distinct so the rate limiter doesn't collapse
        // the second one): the first fits under the 1 MiB cap alone, but the second pushes
        // the held-open stream over it, forcing a real rotate (close, rename, revalidate,
        // reopen) through the freshly created, fully protected ACL. The third (small)
        // write lands in the fresh post-rotation file.
        log.Write(new string('b', 600 * 1024) + "-1");
        log.Write(new string('b', 600 * 1024) + "-2");
        log.Write("fresh line after rotation");

        var logPath = Path.Combine(logs, "sampler.log");
        Assert.True(File.Exists(logPath + ".1"));
        Assert.True(new FileInfo(logPath + ".1").Length > 0);
        Assert.Contains("fresh line after rotation", ReadLogText(logPath), StringComparison.Ordinal);
    }

    private static string ReadLogText(string path)
    {
        // File.ReadAllText/ReadAllLines default to FileShare.Read, which is incompatible
        // with a concurrently open Write handle (SamplerFileLog holds one for the process
        // lifetime): opening with FileShare.ReadWrite here is what actually makes reading
        // the log mid-test possible without disposing the log under test.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadLogLines(string path) =>
        ReadLogText(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
