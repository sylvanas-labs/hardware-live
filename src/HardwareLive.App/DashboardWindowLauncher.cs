using System.ComponentModel;
using System.Diagnostics;

namespace HardwareLive.App;

/// <summary>
/// Opens the dashboard URL in an Edge app window (docs/SPEC.md Component 8 step 5), falling
/// back to the OS default browser when Edge isn't installed. Launched via
/// <c>UseShellExecute = true</c> from this already-unelevated process, so the browser is
/// never accidentally started elevated.
/// </summary>
public static class DashboardWindowLauncher
{
    public static void Launch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msedge",
                Arguments = $"--app={url}",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            // Edge isn't installed or isn't on PATH: fall back to whatever the user has set
            // as their default browser via the OS URL-open verb.
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
    }
}
