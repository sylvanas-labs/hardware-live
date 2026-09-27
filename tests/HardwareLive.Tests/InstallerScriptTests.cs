using System.Diagnostics;

namespace HardwareLive.Tests;

/// <summary>
/// Runs the installer's own script-quality gates (docs/SPEC.md Component 8 "Tests") as part
/// of the normal test pass: the PS 5.1 parse-sweep + ASCII scan (tools/check-scripts.ps1) and
/// the admin-free InstallLib unit tests (tools/test-installlib.ps1). Both are plain scripts
/// that already print their own pass/fail summary and set an exit code; this just shells out
/// to Windows PowerShell 5.1 and asserts that exit code, so a regression in either script
/// fails `dotnet test`, not just a manual run.
/// </summary>
public sealed class InstallerScriptTests
{
    [PowerShellFact]
    public void CheckScriptsPasses()
    {
        var (exitCode, output) = RunPowerShellScript("check-scripts.ps1");
        Assert.True(exitCode == 0, $"tools/check-scripts.ps1 failed (exit {exitCode}):\n{output}");
    }

    [PowerShellFact]
    public void InstallLibUnitTestsPass()
    {
        var (exitCode, output) = RunPowerShellScript("test-installlib.ps1");
        Assert.True(exitCode == 0, $"tools/test-installlib.ps1 failed (exit {exitCode}):\n{output}");
    }

    private static (int ExitCode, string Output) RunPowerShellScript(string scriptFileName)
    {
        var scriptPath = Path.Combine(FindRepositoryRoot(), "tools", scriptFileName);
        Assert.True(File.Exists(scriptPath), $"Expected script not found: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellFactAttribute.PowerShellExePath,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(120_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{scriptFileName} timed out after 120s.");
        }

        return (process.ExitCode, stdout + stderr);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HardwareLive.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Hardware Live repository root.");
    }
}
