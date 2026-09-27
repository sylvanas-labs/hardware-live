using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HardwareLive.Core.Fps;

/// <summary>
/// A Win32 Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> (docs/SPEC.md step7-fps
/// item 1: "so a crashed app never orphans PresentMon"): assigning the PresentMon process to
/// this job means Windows kills it automatically the moment the job handle closes, even if
/// <c>hardware-live.exe</c> itself crashes and never runs its own cleanup code. Needs no
/// elevation -- a normal user can create and assign to a job object for a process they own.
/// </summary>
public sealed class PresentMonJobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    private PresentMonJobObject(SafeFileHandle handle) => _handle = handle;

    /// <summary>Best-effort: returns null (never throws) if job creation or assignment fails,
    /// so a machine policy that blocks job objects degrades to "no auto-kill on crash"
    /// instead of preventing FPS capture entirely.</summary>
    public static PresentMonJobObject? TryCreateAndAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    handle.Dispose();
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(handle, process.SafeHandle))
            {
                handle.Dispose();
                return null;
            }

            return new PresentMonJobObject(handle);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Closes the job handle, which (per <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>)
    /// immediately terminates every process still assigned to it.</summary>
    public void Dispose() => _handle.Dispose();

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, IntPtr jobObjectInfo, uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeHandle process);
}
