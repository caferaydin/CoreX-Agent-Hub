using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Interop;

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>
/// (architecture F.5 #1).
///
/// Process.Kill(entireProcessTree: true) is not treated as sufficient: it walks a parent
/// chain that a descendant can escape. Job membership is inherited by every process a job
/// member creates, so terminating or closing the job kills the whole tree.
///
/// The handle is a SafeHandle, so the job is released deterministically and never leaks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class JobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private JobObjectHandle(IntPtr existingHandle) : base(ownsHandle: true) => SetHandle(existingHandle);

    /// <summary>Creates an unnamed job whose members die when the job is closed.</summary>
    public static JobObjectHandle CreateKillOnClose()
    {
        // LibraryImport cannot marshal a SafeHandle return value, so the raw handle is
        // wrapped immediately and never escapes untracked.
        var raw = CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateJobObject failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        var job = new JobObjectHandle(raw);

        var information = default(JobObjectExtendedLimitInformationNative);
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        unsafe
        {
            var size = (uint)sizeof(JobObjectExtendedLimitInformationNative);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &information, size))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new InvalidOperationException(
                    $"SetInformationJobObject failed with Win32 error {error}.");
            }
        }

        return job;
    }

    /// <summary>
    /// Adds a process to the job. Every process it later creates joins the job as well.
    /// </summary>
    public bool TryAssign(SafeProcessHandle processHandle, out int win32Error)
    {
        win32Error = 0;

        if (AssignProcessToJobObject(this, processHandle))
        {
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>Kills every process still in the job. Safe to call more than once.</summary>
    public void Terminate(uint exitCode)
    {
        if (IsInvalid || IsClosed)
        {
            return;
        }

        _ = TerminateJobObject(this, exitCode);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(
        JobObjectHandle job,
        uint informationClass,
        void* information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(JobObjectHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(JobObjectHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationNative
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersNative
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformationNative BasicLimitInformation;
        public IoCountersNative IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
