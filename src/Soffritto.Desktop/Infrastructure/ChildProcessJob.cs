using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// ProcessRunner kills child processes only when waiting on them is cancelled: if the exe dies
/// some other way (crash, kill, logoff) git, devtunnel or dotnet ef would be left orphaned.
/// With the process inside a kill-on-close job, the system terminates the whole tree by closing
/// the job's last handle when the exe dies.
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Lets DetachedLauncher start outside the job whatever must outlive the exe
    // (Visual Studio, VS Code, the Kestrel, dev server and app consoles).
    private const uint JobObjectLimitBreakawayOk = 0x0800;

    // Kept for the whole life of the process: closing it would terminate the children.
    private static IntPtr job;

    public static bool IsAttached => job != IntPtr.Zero;

    public static bool TryAttachCurrentProcess()
    {
        try
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk,
                },
            };

            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    ref info,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
                || !AssignProcessToJobObject(handle, Process.GetCurrentProcess().Handle))
            {
                CloseHandle(handle);
                return false;
            }

            job = handle;
            return true;
        }
        catch
        {
            return false;
        }
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
