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
    public static string Resolve(string appBaseDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perUserPath = Path.Combine(localAppData, "HardwareLive", "config.json");
        if (File.Exists(perUserPath))
        {
            return perUserPath;
        }

        return Path.Combine(appBaseDirectory, "config.json");
    }
}
