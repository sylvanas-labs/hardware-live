namespace HardwareLive.Core;

/// <summary>
/// Resolves the single effective <c>config.json</c> path (docs/SPEC.md "Config" section):
/// the per-user, always-writable <c>%LOCALAPPDATA%\HardwareLive\config.json</c> takes
/// priority; the app-base-directory file (the only location step-1 ever wrote to, and the
/// only one writable when installed unelevated into a read-only Program Files tree) is a
/// backward-compatible fallback used only when the LOCALAPPDATA file doesn't exist. Never
/// creates either path -- an absent file just means "use defaults".
/// </summary>
public static class ConfigPaths
{
    /// <param name="perUserDirectory">Overrides <c>%LOCALAPPDATA%\HardwareLive</c> so tests
    /// never depend on whether this machine has Hardware Live installed.</param>
    public static string Resolve(string appBaseDirectory, string? perUserDirectory = null)
    {
        var perUserPath = Path.Combine(
            perUserDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareLive"),
            "config.json");
        if (File.Exists(perUserPath))
        {
            return perUserPath;
        }

        return Path.Combine(appBaseDirectory, "config.json");
    }

    /// <summary>Resolves the notes.json path (docs/SPEC.md Component 7): normally
    /// <c>%LOCALAPPDATA%\HardwareLive\notes.json</c>, but the containing directory is
    /// injectable so tests never touch the real per-user profile.</summary>
    public static string ResolveNotesPath(string? notesDirectory = null)
    {
        var directory = notesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareLive");
        return Path.Combine(directory, "notes.json");
    }

    /// <summary>Resolves the directory containing the per-user layouts.json document.</summary>
    public static string ResolveLayoutsDirectory(string? layoutsDirectory = null) =>
        layoutsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareLive");
}
