using System.Runtime.InteropServices;
using System.Text;

namespace HardwareLive.Core.Fps;

/// <summary>
/// Production <see cref="IForegroundWindowProvider"/>: the real Win32 queries (docs/SPEC.md
/// r4.1) -- <c>GetForegroundWindow</c> -&gt; PID, whether that window's rect covers its whole
/// monitor (<c>MonitorFromWindow</c> + <c>GetMonitorInfo</c> vs <c>GetWindowRect</c>, 1 px
/// tolerance), the process name, and its session id (<c>ProcessIdToSessionId</c>).
/// </summary>
public sealed class Win32ForegroundWindowProvider : IForegroundWindowProvider
{
    private const int ToleranceLogicalPixels = 1;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public ForegroundWindowSnapshot? GetForegroundWindow()
    {
        var hwnd = GetForegroundWindow_();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        if (!ProcessIdToSessionId(processId, out var sessionId))
        {
            return null;
        }

        var coversWholeMonitor = CoversWholeMonitor(hwnd);
        var processName = TryGetProcessName(processId) ?? string.Empty;
        if (processName.Length == 0)
        {
            return null;
        }

        return new ForegroundWindowSnapshot((int)processId, processName, (int)sessionId, coversWholeMonitor);
    }

    private static bool CoversWholeMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return Math.Abs(windowRect.Left - info.rcMonitor.Left) <= ToleranceLogicalPixels &&
            Math.Abs(windowRect.Top - info.rcMonitor.Top) <= ToleranceLogicalPixels &&
            Math.Abs(windowRect.Right - info.rcMonitor.Right) <= ToleranceLogicalPixels &&
            Math.Abs(windowRect.Bottom - info.rcMonitor.Bottom) <= ToleranceLogicalPixels;
    }

    private static string? TryGetProcessName(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var capacity = 4096u;
        var buffer = new StringBuilder((int)capacity);
        if (!QueryFullProcessImageName(process, 0, buffer, ref capacity))
        {
            return null;
        }

        var path = buffer.ToString();
        return path.Length == 0 ? null : Path.GetFileNameWithoutExtension(path);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow_();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder imagePath, ref uint size);
}
