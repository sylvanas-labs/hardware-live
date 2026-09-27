namespace HardwareLive.Tests;

/// <summary>
/// A fact that needs Windows PowerShell 5.1 on the machine (installer scripts target 5.1, not
/// pwsh 7). Skipped with a clear reason, not failed, when it's absent -- mirrors
/// <see cref="AdminFactAttribute"/>'s pattern.
/// </summary>
public sealed class PowerShellFactAttribute : FactAttribute
{
    internal static readonly string PowerShellExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public PowerShellFactAttribute()
    {
        if (!File.Exists(PowerShellExePath))
        {
            Skip = $"Requires Windows PowerShell 5.1 ({PowerShellExePath} not found).";
        }
    }
}
