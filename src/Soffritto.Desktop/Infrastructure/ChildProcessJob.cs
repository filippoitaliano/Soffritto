using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// ProcessRunner uccide i processi figli solo quando la loro attesa viene cancellata: se l'exe
/// muore in altro modo (crash, kill, logoff) git, devtunnel o dotnet ef resterebbero orfani.
/// Con il processo dentro un job kill-on-close e' il sistema, chiudendo l'ultimo handle del job
/// alla morte dell'exe, a terminare tutto l'albero.
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Permette a DetachedLauncher di avviare fuori dal job cio' che deve sopravvivere all'exe
    // (Visual Studio, VS Code, le console di Kestrel, dev server e app).
    private const uint JobObjectLimitBreakawayOk = 0x0800;

    // Tenuto per tutta la vita del processo: chiuderlo terminerebbe i figli.
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
