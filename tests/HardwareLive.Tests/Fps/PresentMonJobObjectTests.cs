using System.Diagnostics;
using HardwareLive.Core.Fps;

namespace HardwareLive.Tests.Fps;

/// <summary>Real Job Object behavior (docs/SPEC.md step7-fps: "Job Object with
/// KILL_ON_JOB_CLOSE preferred so a crashed app never orphans PresentMon; if you implement it
/// via P/Invoke, test it"). Needs no elevation -- a normal user can create/assign a job for a
/// process they own.</summary>
public sealed class PresentMonJobObjectTests
{
    [Fact]
    public async Task DisposingTheJobKillsTheAssignedProcessPromptly()
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start cmd.exe.");
        try
        {
            var job = PresentMonJobObject.TryCreateAndAssign(process);
            Assert.NotNull(job);

            Assert.False(process.HasExited);

            job!.Dispose();

            var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
            Assert.True(exited, "Process should have been killed within 5 seconds of the job handle closing.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
