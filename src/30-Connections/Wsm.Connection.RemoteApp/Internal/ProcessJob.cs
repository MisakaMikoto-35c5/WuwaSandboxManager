using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// A process-wide Windows job object configured with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, used to bind every launched
/// <c>mstsc.exe</c> (and the client processes it spawns) to the lifetime of the
/// hosting process.
/// <para>
/// RAIL sessions are presented by <c>wsm connect</c> child processes; the GUI can
/// only end those children. Without the job, killing the child would orphan a
/// live <c>mstsc.exe</c> (and its guest session). With it, the last job handle
/// closing — including a hard kill of the host process — terminates the client.
/// </para>
/// </summary>
internal static class ProcessJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> SharedJob = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Binds <paramref name="process"/> to the shared job. Best effort: process
    /// nesting rules can refuse the assignment, which only means the process is
    /// not auto-killed with the host.
    /// </summary>
    public static void Bind(Process process)
    {
        try
        {
            var job = SharedJob.Value;
            if (job == IntPtr.Zero || process.HasExited)
            {
                return;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                Debug.WriteLine(
                    $"Binding process {process.Id} to the kill-on-close job failed (win32={Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Binding process {process.Id} to the kill-on-close job failed: {ex.Message}");
        }
    }

    private static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var information = new ExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, pointer, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct ExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

