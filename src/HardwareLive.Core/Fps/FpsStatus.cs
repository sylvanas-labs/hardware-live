namespace HardwareLive.Core.Fps;

/// <summary>Exact wire-format <c>fps.status</c> strings for <c>/api/snapshot</c>'s top-level
/// <c>fps</c> field (docs/SPEC.md step7-fps item 5).</summary>
public static class FpsStatus
{
    public const string Disabled = "disabled";
    public const string Starting = "starting";
    public const string NotInstalled = "not-installed";
    public const string IntegrityFailed = "integrity-failed";
    public const string NeedsPermission = "needs-permission";
    public const string UnexpectedOutput = "unexpected-output";
    public const string Unavailable = "unavailable";
    public const string NoTarget = "no-target";
    public const string Tracking = "tracking";
}

/// <summary>The current FPS status shown by <c>/api/snapshot</c>'s <c>fps</c> field
/// (docs/SPEC.md step7-fps item 5: <c>fps.app</c> is a string, so it's exposed here rather
/// than as a sensor).</summary>
public sealed record FpsSnapshotInfo(string Status, string? App, int? Pid)
{
    public static readonly FpsSnapshotInfo Disabled = new(FpsStatus.Disabled, null, null);
}

public interface IFpsSnapshotProvider
{
    FpsSnapshotInfo Current { get; }
}

/// <summary>The write side of FPS settings (docs/SPEC.md step7-fps item 7): the token-guarded
/// <c>PUT /api/settings</c> extensions <c>fpsEnabled</c>/<c>fpsDenylistAdd</c>/<c>fpsPin</c>.</summary>
public interface IFpsSettingsWriter
{
    void SetEnabled(bool enabled);

    void AddToDenylist(string processName);

    void SetPinnedProcess(string? processName);
}

/// <summary>Combines the read and write sides so <c>RequestRouter</c> needs only one
/// constructor parameter.</summary>
public interface IFpsController : IFpsSnapshotProvider, IFpsSettingsWriter;
