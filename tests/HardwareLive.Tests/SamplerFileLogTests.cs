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
        var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: false);

        log.Write("should never land on disk");

        Assert.False(Directory.Exists(_baseDirectory));
    }

    [AdminFact]
    public void PrivilegedProcessCreatesTheDirectoryAndWritesTheMessage()
    {
        var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: true);

        log.Write("hello sampler");

        var logPath = Path.Combine(_baseDirectory, "sampler.log");
        Assert.True(File.Exists(logPath));
        Assert.Contains("hello sampler", File.ReadAllText(logPath), StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeLogRotatesToDotOneAndStartsFresh()
    {
        Directory.CreateDirectory(_baseDirectory);
        var logPath = Path.Combine(_baseDirectory, "sampler.log");
        File.WriteAllText(logPath, new string('a', 1024 * 1024 + 1));

        // The pre-existing directory wasn't created with our production ACL (it's a plain
        // temp dir owned by the current test process), so bypass the owner check here;
        // the point of this test is the size-based rotation, covered separately from the
        // owner-trust check.
        var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);

        log.Write("fresh line after rotation");

        var rotatedPath = logPath + ".1";
        Assert.True(File.Exists(rotatedPath));
        Assert.True(new FileInfo(rotatedPath).Length > 1024 * 1024);
        var freshContent = File.ReadAllText(logPath);
        Assert.Contains("fresh line after rotation", freshContent, StringComparison.Ordinal);
        Assert.True(new FileInfo(logPath).Length < 1024 * 1024);
    }

    [AdminFact]
    public void IdenticalMessagesAreRateLimitedToOncePerWindowThenReportRepeatCount()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var log = new SamplerFileLog(_baseDirectory, clock, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);
        var logPath = Path.Combine(_baseDirectory, "sampler.log");

        log.Write("duplicate message");
        log.Write("duplicate message");
        log.Write("duplicate message");

        var linesAfterBurst = File.ReadAllLines(logPath);
        Assert.Single(linesAfterBurst);

        clock.Advance(TimeSpan.FromMilliseconds(59_999));
        log.Write("duplicate message"); // still within the 60s window
        Assert.Single(File.ReadAllLines(logPath));

        clock.Advance(TimeSpan.FromMilliseconds(2));
        log.Write("duplicate message"); // now past the window
        var linesAfterWindow = File.ReadAllLines(logPath);

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

        var log = new SamplerFileLog(reparsedLogs, isPrivilegedOverride: true, isTrustedOwnerOverride: _ => true);

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
        var log = new SamplerFileLog(_baseDirectory, isPrivilegedOverride: true);

        var exception = Record.Exception(() => log.Write("must not be written"));

        Assert.Null(exception);
        Assert.False(File.Exists(Path.Combine(_baseDirectory, "sampler.log")));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
