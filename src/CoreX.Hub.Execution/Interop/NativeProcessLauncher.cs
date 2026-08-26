using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CoreX.Hub.Execution.Processes;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Interop;

/// <summary>Why a native launch did not produce a running child.</summary>
internal enum NativeLaunchFailure
{
    None,
    Pipes,
    Job,
    Create,
    Assign,
    Resume,
    CancelledBeforeResume,
}

/// <summary>The outcome of one launch attempt. Exactly one of the two halves is meaningful.</summary>
internal readonly record struct NativeLaunchOutcome(
    NativeChildProcess? Child,
    NativeLaunchFailure Failure,
    string? Message)
{
    public static NativeLaunchOutcome Started(NativeChildProcess child) => new(child, NativeLaunchFailure.None, null);

    public static NativeLaunchOutcome Failed(NativeLaunchFailure failure, string message) =>
        new(null, failure, message);
}

/// <summary>
/// Creates a Windows child that is inside the Hub's Job Object before it executes anything.
///
/// This is the whole point of the component. The managed <see cref="System.Diagnostics.Process"/>
/// API can only start a process already running, so the job could not be attached until after
/// the child's first instruction - and a child that spawns something in that window leaves a
/// descendant the Hub can neither account for nor kill. The window was small, documented and
/// real, and no amount of ordering in managed code could close it.
///
/// So the order here is not a style choice, it is the guarantee:
///
///   1. the job exists and is configured;
///   2. pipes, command line and environment are prepared;
///   3. the child is created <c>CREATE_SUSPENDED</c> - it exists, it has run nothing;
///   4. it is assigned to the job;
///   5. the parent drops its copies of the child's pipe ends;
///   6. only now is the primary thread resumed.
///
/// If step 4 does not succeed the child is terminated where it stands. There is no path that
/// resumes an unassigned child, and no fallback that starts one without a job.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private const int StartfUseStdHandles = 0x00000100;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    private static readonly IntPtr ProcThreadAttributeHandleList = 0x00020002;

    private const uint ResumeThreadFailed = 0xFFFFFFFF;

    /// <summary>The error an injected fault reports, so a simulated failure looks like a real one.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// Starts the child, or returns why it could not be started - never a half-started one.
    /// </summary>
    public static NativeLaunchOutcome Launch(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        JobObjectHandle job,
        ProcessLaunchFaults faults,
        CancellationToken cancellationToken)
    {
        WindowsPipe? standardInput = null;
        WindowsPipe? standardOutput = null;
        WindowsPipe? standardError = null;
        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;

        var attributeList = IntPtr.Zero;
        var commandLineBuffer = IntPtr.Zero;
        var started = false;

        try
        {
            try
            {
                standardInput = WindowsPipe.Create(childReads: true);
                standardOutput = WindowsPipe.Create(childReads: false);
                standardError = WindowsPipe.Create(childReads: false);
            }
            catch (InvalidOperationException ex)
            {
                return NativeLaunchOutcome.Failed(NativeLaunchFailure.Pipes, ex.Message);
            }

            // Exactly three handles may cross into the child. Naming them explicitly is what
            // stops every other inheritable handle this process happens to own from going
            // with them - stdio redirection alone does not restrict inheritance.
            var childHandles = stackalloc IntPtr[3];
            childHandles[0] = standardInput.ChildEnd.DangerousGetHandle();
            childHandles[1] = standardOutput.ChildEnd.DangerousGetHandle();
            childHandles[2] = standardError.ChildEnd.DangerousGetHandle();

            if (!TryCreateAttributeList(childHandles, 3, out attributeList, out var attributeError))
            {
                return NativeLaunchOutcome.Failed(NativeLaunchFailure.Create, attributeError!);
            }

            var startup = default(StartupInfoEx);
            startup.StartupInfo.cb = sizeof(StartupInfoEx);
            startup.StartupInfo.dwFlags = StartfUseStdHandles | StartfUseShowWindow;
            startup.StartupInfo.wShowWindow = SwHide;
            startup.StartupInfo.hStdInput = childHandles[0];
            startup.StartupInfo.hStdOutput = childHandles[1];
            startup.StartupInfo.hStdError = childHandles[2];
            startup.AttributeList = attributeList;

            // CreateProcessW is documented as able to write into the command line buffer, so
            // it gets its own native copy rather than a pinned managed string.
            commandLineBuffer = Marshal.StringToHGlobalUni(
                WindowsCommandLine.Build(executable, arguments));

            var environmentBlock = WindowsEnvironmentBlock.Render(environment);

            var information = default(ProcessInformation);
            bool created;
            int createError;

            if (faults.FailCreateProcess)
            {
                created = false;
                createError = 2; // ERROR_FILE_NOT_FOUND, a real CreateProcess refusal shape.
            }
            else
            {
                fixed (char* applicationName = executable)
                fixed (char* currentDirectory = workingDirectory)
                fixed (char* block = environmentBlock)
                {
                    created = CreateProcessW(
                        applicationName,
                        (char*)commandLineBuffer,
                        IntPtr.Zero,
                        IntPtr.Zero,

                        // Required for the handle list to mean anything; the list is what
                        // keeps it from meaning everything.
                        bInheritHandles: true,
                        CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow | ExtendedStartupInfoPresent,
                        block,
                        currentDirectory,
                        &startup,
                        &information);

                    createError = created ? 0 : Marshal.GetLastWin32Error();
                }
            }

            if (!created)
            {
                return NativeLaunchOutcome.Failed(
                    NativeLaunchFailure.Create,
                    $"CreateProcess failed with Win32 error {createError}.");
            }

            processHandle = new SafeProcessHandle(information.hProcess, ownsHandle: true);
            threadHandle = new SafeThreadHandle(information.hThread);

            // The step this whole component exists for. Nothing has executed yet.
            var assignError = ErrorAccessDenied;
            var assigned = !faults.FailAssign && job.TryAssign(processHandle, out assignError);

            if (!assigned)
            {
                return NativeLaunchOutcome.Failed(
                    NativeLaunchFailure.Assign,
                    $"AssignProcessToJobObject failed with Win32 error {assignError}; the process tree could not be bounded.");
            }

            // The child now owns its ends. Holding duplicates would keep every pipe open
            // past the child's exit, and the readers would never see end of stream.
            standardInput.CloseChildEnd();
            standardOutput.CloseChildEnd();
            standardError.CloseChildEnd();

            faults.BeforeResume?.Invoke();

            // A run cancelled before the child ever ran must leave nothing behind that ran.
            // Resuming it merely so the ordinary cancellation path can kill it would execute
            // the very code the caller withdrew.
            if (cancellationToken.IsCancellationRequested)
            {
                return NativeLaunchOutcome.Failed(
                    NativeLaunchFailure.CancelledBeforeResume,
                    "The run was cancelled before the child was resumed.");
            }

            var resumeError = ErrorAccessDenied;
            var resumed = !faults.FailResume && ResumeThread(threadHandle) != ResumeThreadFailed;

            if (!resumed)
            {
                if (!faults.FailResume)
                {
                    resumeError = Marshal.GetLastWin32Error();
                }

                return NativeLaunchOutcome.Failed(
                    NativeLaunchFailure.Resume,
                    $"ResumeThread failed with Win32 error {resumeError}; the child could not be started.");
            }

            var child = new NativeChildProcess(
                processHandle,
                information.dwProcessId,
                standardInput,
                standardOutput,
                standardError);

            started = true;

            return NativeLaunchOutcome.Started(child);
        }
        finally
        {
            // Everything below runs on every path. On the failure paths it is what guarantees
            // the promise: a child that exists but was never resumed is terminated here, so no
            // unbounded and no permanently frozen process can outlive this method.
            if (!started && processHandle is not null)
            {
                TerminateQuietly(processHandle);
            }

            threadHandle?.Dispose();

            if (!started)
            {
                processHandle?.Dispose();
                standardInput?.Dispose();
                standardOutput?.Dispose();
                standardError?.Dispose();
            }

            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (commandLineBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandLineBuffer);
            }
        }
    }

    /// <summary>
    /// Builds the attribute list naming the only handles the child may inherit.
    ///
    /// Two calls by design: the first asks how much room the list needs, the second fills it.
    /// The first is expected to fail with ERROR_INSUFFICIENT_BUFFER and that is not an error.
    /// </summary>
    private static bool TryCreateAttributeList(
        IntPtr* handles,
        int count,
        out IntPtr attributeList,
        out string? failure)
    {
        attributeList = IntPtr.Zero;
        failure = null;

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        if (size == IntPtr.Zero)
        {
            failure = $"InitializeProcThreadAttributeList could not size the list (Win32 error {Marshal.GetLastWin32Error()}).";
            return false;
        }

        var list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            failure = $"InitializeProcThreadAttributeList failed with Win32 error {error}.";
            return false;
        }

        if (!UpdateProcThreadAttribute(
                list,
                0,
                ProcThreadAttributeHandleList,
                handles,
                (IntPtr)(count * sizeof(IntPtr)),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            failure = $"UpdateProcThreadAttribute failed with Win32 error {error}.";
            return false;
        }

        attributeList = list;
        return true;
    }

    private static void TerminateQuietly(SafeProcessHandle process)
    {
        try
        {
            TerminateProcess(process, 1);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The child may already be gone. Failing to kill something that has exited is
            // not a condition worth reporting from a cleanup path.
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint creationFlags,
        char* environment,
        char* currentDirectory,
        StartupInfoEx* startupInfo,
        ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeThreadHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        void* value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
