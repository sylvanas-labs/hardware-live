using System.Diagnostics;
using System.Text;

namespace HardwareLive.Core.Fps;

/// <summary>
/// Production <see cref="IPresentMonProcessLauncher"/>: spawns PresentMon unelevated with
/// <c>UseShellExecute=false</c>/<c>CreateNoWindow=true</c>, redirects stdout as UTF-8
/// (docs/SPEC.md: "stdout is plain ASCII CSV with LF line endings, so read it as UTF-8" --
/// PowerShell 7's redirection turns it into UTF-16, so this must never shell out through
/// that), drains and discards stderr on a background task (keeping only a bounded tail for
/// access-denied detection), and best-effort assigns the process to a
/// <see cref="PresentMonJobObject"/> so it never survives a crashed parent.
/// </summary>
public sealed class Win32PresentMonProcessLauncher : IPresentMonProcessLauncher
{
    public IManagedProcess Start(string exePath, string arguments)
    {
        var startInfo = new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");

        return new RealManagedProcess(process);
    }

    private sealed class RealManagedProcess : IManagedProcess
    {
        private const int MaxStderrTailChars = 4096;

        private readonly Process _process;
        private readonly PresentMonJobObject? _job;
        private readonly StringBuilder _stderrTail = new();
        private readonly object _stderrLock = new();
        private readonly Task _stderrDrainTask;

        public RealManagedProcess(Process process)
        {
            _process = process;
            _job = PresentMonJobObject.TryCreateAndAssign(process);
            _stderrDrainTask = DrainStandardErrorAsync();
        }

        public int Id => _process.Id;

        public string StandardErrorTail
        {
            get
            {
                lock (_stderrLock)
                {
                    return _stderrTail.ToString();
                }
            }
        }

        public Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
            _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return _process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the call: nothing to do.
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // Best effort only: draining stderr must never block shutdown.
            }

            _job?.Dispose();
            _process.Dispose();
        }

        private async Task DrainStandardErrorAsync()
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync()) is not null)
                {
                    lock (_stderrLock)
                    {
                        _stderrTail.Append(line).Append('\n');
                        if (_stderrTail.Length > MaxStderrTailChars)
                        {
                            _stderrTail.Remove(0, _stderrTail.Length - MaxStderrTailChars);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The pipe closed underneath us (process killed mid-drain): nothing to do.
            }
        }
    }
}
