using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CoreX.Hub.Execution.Processes;
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

    private JobObjectHandle(IntPtr existingHandle) : base(ownsHandle: true) => SetHandle(existingHandle);

    /// <summary>
    /// Creates an unnamed job whose members die when the job is closed, carrying whatever
    /// resource ceilings were requested.
    ///
    /// Configuration is part of creation rather than a later step, so there is no window in
    /// which a job exists with weaker limits than the caller asked for - and no way to obtain
    /// a job that was supposed to be limited but silently is not. If the limits cannot be
    /// applied this throws, and the caller starts nothing.
    /// </summary>
    /// <param name="limits">The ceilings to enforce. <see cref="ProcessResourceLimits.None"/> asks for none.</param>
    /// <param name="simulateConfigurationFailure">
    /// Test only. Makes the configuration step fail the way Windows would, so the fail closed
    /// path can be exercised - it cannot be provoked otherwise on a healthy machine.
    /// </param>
    public static JobObjectHandle Create(
        ProcessResourceLimits limits,
        bool simulateConfigurationFailure = false)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var plan = JobLimitPlan.For(limits);
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
        information.BasicLimitInformation.LimitFlags = plan.LimitFlags;
        information.BasicLimitInformation.ActiveProcessLimit = plan.ActiveProcessLimit;
        information.ProcessMemoryLimit = (nuint)plan.ProcessMemoryLimitBytes;
        information.JobMemoryLimit = (nuint)plan.JobMemoryLimitBytes;

        unsafe
        {
            var size = (uint)sizeof(JobObjectExtendedLimitInformationNative);

            var configured = !simulateConfigurationFailure
                && SetInformationJobObject(job, JobObjectExtendedLimitInformation, &information, size);

            if (!configured)
            {
                // Fail closed. A caller that asked for a ceiling and cannot have it must not
                // be handed a job that merely reaps its tree: the request was about how much
                // of the machine a run may take, and answering it with a different policy
                // would be answering a different question.
                var error = simulateConfigurationFailure ? 5 : Marshal.GetLastWin32Error();
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
