using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Interop;

/// <summary>
/// A running child created by <see cref="NativeProcessLauncher"/>, and the handful of things
/// the supervisor above needs to know about it.
///
/// It deliberately mirrors the small part of <see cref="System.Diagnostics.Process"/> that
/// <c>ProcessRunner</c> was already using - exit state, exit code, id, three streams - so that
/// replacing how a child is created did not become an excuse to redesign how one is watched.
/// The supervision, the three deadlines and the result shape are unchanged; only the thing
/// they are pointed at is new.
///
/// Exit is observed with a wait handle rather than by polling an exit code. The obvious
/// alternative reads <c>GetExitCodeProcess</c> and compares against STILL_ACTIVE, which is
/// the value 259 - indistinguishable from a child that genuinely exited with 259. Waiting on
/// the process handle has no such ambiguity.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeChildProcess : IDisposable
{
    private const uint WaitObject0 = 0x00000000;

    private readonly SafeProcessHandle _process;
    private readonly WindowsPipe _standardInput;
    private readonly WindowsPipe _standardOutput;
    private readonly WindowsPipe _standardError;
    private readonly TaskCompletionSource _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ManualResetEvent _exitEvent;
    private readonly RegisteredWaitHandle _registration;

    private int _disposed;

    internal NativeChildProcess(
        SafeProcessHandle process,
        int id,
        WindowsPipe standardInput,
        WindowsPipe standardOutput,
        WindowsPipe standardError)
    {
        _process = process;
        _standardInput = standardInput;
        _standardOutput = standardOutput;
        _standardError = standardError;

        Id = id;

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        StandardInput = new StreamWriter(
            new FileStream(standardInput.ParentEnd, FileAccess.Write), utf8) { AutoFlush = false };
        StandardOutput = new StreamReader(
            new FileStream(standardOutput.ParentEnd, FileAccess.Read), utf8);
        StandardError = new StreamReader(
            new FileStream(standardError.ParentEnd, FileAccess.Read), utf8);

        // A process handle is signalled when the process ends, so it can be waited on like
        // any other kernel object. The wait handle does not own it - the SafeProcessHandle
        // does - or the handle would be closed twice.
        _exitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(_process.DangerousGetHandle(), ownsHandle: false),
        };

        _registration = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, _) => _exited.TrySetResult(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public int Id { get; }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    /// <summary>Whether the process object is already signalled. Never ambiguous.</summary>
    public bool HasExited => WaitForSingleObject(_process, 0) == WaitObject0;

    /// <summary>The exit code, once there is one. Null while the child is still running.</summary>
    public int? ExitCode =>
        HasExited && GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : null;

    /// <summary>Completes when the child ends, however it ends.</summary>
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _exited.Task.WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Order matters: stop waiting before the handle the wait refers to goes away.
        _registration.Unregister(waitObject: null);
        _exitEvent.Dispose();

        Close(StandardInput);
        Close(StandardOutput);
        Close(StandardError);

        _standardInput.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        _process.Dispose();
    }

    private static void Close(IDisposable stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The other end went away first. Nothing to report from a teardown path.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
