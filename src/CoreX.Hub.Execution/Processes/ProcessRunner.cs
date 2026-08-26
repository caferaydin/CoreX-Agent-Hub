using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Interop;
using CoreX.Hub.Execution.Text;

namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// The single place in production code allowed to create an operating system child
/// process (architecture E.1 #1, ADR-0006).
///
/// It exists so that timeout handling, process tree termination, stream draining,
/// encoding and environment isolation are applied once, consistently, instead of being
/// reinvented by every caller. It knows nothing about tasks, executions, agents, reviews
/// or git.
///
/// Guarantees:
///   * no shell: an executable plus an argument vector, never a command line, and the
///     request boundary refuses a shell interpreter or script shim outright
///     (see <see cref="ShellExecutablePolicy"/>);
///   * the child starts from an empty environment (ADR-0007);
///   * every descendant belongs to a Windows Job Object, so ending the run ends the tree;
///   * stdout and stderr are drained concurrently from the moment the process starts;
///   * three explicit deadlines - soft, idle and hard.
///
/// The child is inside the job before it executes anything. Until Phase 10 this was the one
/// hole in the list above: <c>Process.Start</c> returns a process that is already running, so
/// the job could only be attached afterwards, and anything the child spawned in that window
/// escaped it. The window was small and real, and it was documented as accepted rather than
/// pretended away. It is now closed - the child is created suspended, assigned, and only then
/// resumed (see <see cref="Interop.NativeProcessLauncher"/>) - and there is no remaining path
/// that starts a child without a job.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessRunner
{
    /// <summary>
    /// Placed in GIT_ASKPASS so git can never obtain credentials interactively. It points
    /// at a path that does not exist, which needs no shell and no shipped helper binary;
    /// combined with GIT_TERMINAL_PROMPT=0 the child fails closed instead of hanging.
    /// </summary>
    public const string DisabledAskPassFileName = "corex-askpass-disabled";

    private const int ReadChunkCharacters = 8 * 1024;
    private static readonly TimeSpan SupervisorInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(5);

    private readonly FileSystemGuard _guard;
    private readonly ProcessLaunchFaults _faults;

    public ProcessRunner(FileSystemGuard guard) : this(guard, ProcessLaunchFaults.None)
    {
    }

    /// <summary>
    /// Used only by this assembly's tests, to make one native launch step fail on purpose.
    ///
    /// The guarantees worth having here are about the failure paths, and those cannot be
    /// reached by hoping the machine misbehaves. Carried per instance rather than as a static
    /// switch so an injected fault cannot leak into a run happening beside it.
    /// </summary>
    internal ProcessRunner(FileSystemGuard guard, ProcessLaunchFaults faults)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));
    }

    /// <summary>Values forced onto every child regardless of the allowlist.</summary>
    public static IReadOnlyDictionary<string, string> ForcedEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["NO_COLOR"] = "1",
            ["CI"] = "1",
            ["GIT_ASKPASS"] = Path.Combine(AppContext.BaseDirectory, DisabledAskPassFileName),
        };

    public FileSystemGuard Guard => _guard;

    public async Task<ProcessRunResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Phase 2 ProcessRunner requires Windows Job Object support.");
        }

        var startedAtUtc = DateTimeOffset.UtcNow;

        if (!TryResolveExecutable(request.Executable, out var executable, out var executableFailure))
        {
            return StartFailure(startedAtUtc, executableFailure!);
        }

        var workingDirectory = _guard.ValidateExisting(request.WorkingDirectory);
        if (!workingDirectory.IsAllowed)
        {
            return StartFailure(
                startedAtUtc,
                $"Working directory refused ({workingDirectory.Reason}): {workingDirectory.Detail}");
        }

        if (!Directory.Exists(workingDirectory.CanonicalPath))
        {
            return StartFailure(startedAtUtc, $"Working directory '{workingDirectory.CanonicalPath}' does not exist.");
        }

        return await ExecuteAsync(
            request, executable!, workingDirectory.CanonicalPath!, _faults, startedAtUtc, cancellationToken);
    }

    private static bool TryResolveExecutable(string candidate, out string? executable, out string? failure)
    {
        executable = null;
        failure = null;

        // E.1 #3 runtime boundary. Classification happens first and is purely lexical, so
        // a shell is refused before the child is created and regardless of whether it is even
        // installed on this machine.
        if (ShellExecutablePolicy.IsShellOrScript(candidate, out var shellReason))
        {
            failure = shellReason;
            return false;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            failure =
                $"'{candidate}' is not a fully qualified path. ProcessRunner does not perform PATH lookup, "
                + "so that the resolved executable is deterministic and independent of the child environment.";
            return false;
        }

        // Deliberately no Path.GetFullPath here: canonicalisation and containment are
        // FileSystemGuard's job (E.1 #2), and the executable is intentionally outside the
        // trust root. Requiring a fully qualified existing file is enough to be
        // deterministic.
        if (!File.Exists(candidate))
        {
            failure = $"Executable '{candidate}' does not exist.";
            return false;
        }

        executable = candidate;
        return true;
    }

    private static ProcessRunResult StartFailure(DateTimeOffset startedAtUtc, string message) =>
        new()
        {
            TerminationReason = ProcessTerminationReason.StartFailed,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = DateTimeOffset.UtcNow,
            StartFailureMessage = message,
        };

    private static async Task<ProcessRunResult> ExecuteAsync(
        ProcessRequest request,
        string executable,
        string workingDirectory,
        ProcessLaunchFaults faults,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var stdout = new BoundedOutputBuffer(request.MaxCapturedCharactersPerStream);
        var stderr = new BoundedOutputBuffer(request.MaxCapturedCharactersPerStream);

        // Step one, before anything can be launched. The job is created and fully configured
        // here - ceilings included - so the child is created into a job that already enforces
        // everything asked of it. A job that could not be created, or could not be given the
        // limits the caller requested, is a tree that could not be bounded: nothing is started
        // at all, and there is deliberately no path that falls back to an unbounded child.
        JobObjectHandle job;

        try
        {
            job = faults.FailJobCreation
                ? throw new InvalidOperationException("CreateJobObject failed with Win32 error 5.")
                : JobObjectHandle.Create(request.ResourceLimits, faults.FailJobConfiguration);
        }
        catch (InvalidOperationException ex)
        {
            return StartFailure(startedAtUtc, $"The process job could not be created: {ex.Message}");
        }

        using (job)
        {
            var environment = WindowsEnvironmentBlock.Compose(
                request.EnvironmentAllowlist,
                Environment.GetEnvironmentVariable,
                request.AdditionalEnvironment,
                ForcedEnvironment);

            var launch = NativeProcessLauncher.Launch(
                executable,
                request.Arguments,
                workingDirectory,
                environment,
                job,
                faults,
                cancellationToken);

            if (launch.Child is not { } child)
            {
                // Nothing is running. The launcher terminated whatever it had created before
                // returning, so there is no tree left to reap - but the job is ended anyway
                // because doing so twice costs nothing and missing it once would not.
                job.Terminate(1);

                return launch.Failure == NativeLaunchFailure.CancelledBeforeResume
                    ? new ProcessRunResult
                    {
                        TerminationReason = ProcessTerminationReason.Cancelled,
                        StartedAtUtc = startedAtUtc,
                        EndedAtUtc = DateTimeOffset.UtcNow,
                        JobTerminated = true,
                    }
                    : StartFailure(startedAtUtc, launch.Message ?? $"'{executable}' could not be started.");
            }

            using var process = child;

            return await SuperviseRunAsync(
                request, process, job, stdout, stderr, startedAtUtc, cancellationToken);
        }
    }

    /// <summary>
    /// Watches a child that is already running and already inside the job.
    ///
    /// Unchanged by Phase 10 on purpose: how a run is supervised, when it is given up on and
    /// what is reported are the same decisions they were, and replacing how a child is created
    /// was not an excuse to revisit any of them.
    /// </summary>
    private static async Task<ProcessRunResult> SuperviseRunAsync(
        ProcessRequest request,
        NativeChildProcess process,
        JobObjectHandle job,
        BoundedOutputBuffer stdout,
        BoundedOutputBuffer stderr,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var state = new RunState(request);
        var stdoutPump = PumpAsync(process.StandardOutput, stdout, state, request.StandardOutputObserver);
        var stderrPump = PumpAsync(process.StandardError, stderr, state, observer: null);
        var stdinTask = FeedStdinAsync(process, request, state);

        var reason = await SuperviseAsync(process, job, state, cancellationToken);

        // The process is finished with, so stdin must be released whatever the reason.
        // Without this the stdin task would wait forever on a run that simply exited.
        state.CloseStdin(process);

        await WaitForExitQuietlyAsync(process, DrainWindow);
        await DrainAsync(job, state, stdoutPump, stderrPump);
        await stdinTask;

        var exitedNaturally = !state.JobWasTerminated && process.HasExited;

        // Nothing may outlive the run. Terminating an already empty job is a no-op.
        job.Terminate(1);

        return BuildResult(request, process, state, reason, exitedNaturally, stdout, stderr, startedAtUtc);
    }

    /// <summary>
    /// Reads one stream to end. Both pumps run concurrently from process start; reading
    /// one stream to completion before the other is the classic redirected-pipe deadlock.
    /// The loop keeps consuming the pipe even after the capture ceiling is reached.
    ///
    /// The optional observer sees each chunk before it is captured, so a caller can follow a
    /// run that has not finished. It is called from here rather than from a second reader
    /// because there is only one pipe and it may only be read once.
    /// </summary>
    private static async Task PumpAsync(
        StreamReader reader,
        BoundedOutputBuffer buffer,
        RunState state,
        Action<string>? observer)
    {
        var chunk = new char[ReadChunkCharacters];

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(chunk.AsMemory()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            state.RecordActivity();
            buffer.Append(chunk.AsSpan(0, read));
            Observe(observer, chunk, read);
        }
    }

    /// <summary>
    /// Hands one chunk to the observer without ever letting it end the run.
    ///
    /// A throwing observer would otherwise abandon the pump, leave the pipe unread and stall
    /// the child. The captured output is the authority regardless of what an observer does
    /// with what it sees.
    /// </summary>
    private static void Observe(Action<string>? observer, char[] chunk, int read)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer(new string(chunk, 0, read));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Observation is a courtesy to the caller, never a condition of the run.
        }
    }

    private static async Task FeedStdinAsync(NativeChildProcess process, ProcessRequest request, RunState state)
    {
        try
        {
            if (request.StdinPayload is { } payload)
            {
                await process.StandardInput.WriteAsync(payload).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                state.CloseStdin(process);
                return;
            }

            if (!request.KeepStandardInputOpen)
            {
                // Nothing to send, so say so now. Stdin is always redirected, and a child
                // holding a pipe that never ends can wait on it for ever - which is how a
                // finished build tool becomes a hard timeout.
                state.CloseStdin(process);
                return;
            }

            // Asked for explicitly: the child waits on stdin, and graceful shutdown closes it.
            await state.StdinClosed.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child closed its end first; nothing to report.
        }
    }

    /// <summary>
    /// Watches the three deadlines and external cancellation.
    ///
    ///   soft   graceful: close stdin, allow a bounded grace period, then end the job
    ///   idle   no stdout or stderr activity for the configured interval, then as above
    ///   hard   authoritative wall clock ceiling: the job is terminated immediately
    ///
    /// The hard deadline can never be postponed by output, and it also bounds the grace
    /// period of an earlier soft or idle trigger.
    /// </summary>
    private static async Task<ProcessTerminationReason> SuperviseAsync(
        NativeChildProcess process,
        JobObjectHandle job,
        RunState state,
        CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            if (state.HardElapsed)
            {
                await EndRunAsync(process, job, state, graceful: false);
                return ProcessTerminationReason.HardTimeout;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await EndRunAsync(process, job, state, graceful: true);
                return ProcessTerminationReason.Cancelled;
            }

            if (state.SoftElapsed)
            {
                await EndRunAsync(process, job, state, graceful: true);
                return ProcessTerminationReason.SoftTimeout;
            }

            if (state.IdleElapsed)
            {
                await EndRunAsync(process, job, state, graceful: true);
                return ProcessTerminationReason.IdleTimeout;
            }

            try
            {
                await Task.Delay(SupervisorInterval, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Never observed: the delay is deliberately not cancellable so that the
                // shutdown path above always runs and the tree is always bounded.
            }
        }

        return ProcessTerminationReason.Exited;
    }

    private static async Task EndRunAsync(NativeChildProcess process, JobObjectHandle job, RunState state, bool graceful)
    {
        state.CloseStdin(process);

        if (graceful)
        {
            var grace = state.RemainingGrace;
            if (grace > TimeSpan.Zero)
            {
                await WaitForExitQuietlyAsync(process, grace).ConfigureAwait(false);
            }
        }

        if (process.HasExited)
        {
            return;
        }

        state.JobWasTerminated = true;
        job.Terminate(1);
        await WaitForExitQuietlyAsync(process, DrainWindow).ConfigureAwait(false);
    }

    private static async Task DrainAsync(JobObjectHandle job, RunState state, params Task[] pumps)
    {
        var all = Task.WhenAll(pumps);
        if (await Task.WhenAny(all, Task.Delay(DrainWindow)).ConfigureAwait(false) == all)
        {
            return;
        }

        // A surviving descendant still holds the write end of a pipe. Ending the job
        // closes it so the readers can complete rather than hang forever.
        state.JobWasTerminated = true;
        job.Terminate(1);
        await Task.WhenAny(all, Task.Delay(DrainWindow)).ConfigureAwait(false);
    }

    private static async Task WaitForExitQuietlyAsync(NativeChildProcess process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Still running; the caller decides what to do next.
        }
    }

    private static ProcessRunResult BuildResult(
        ProcessRequest request,
        NativeChildProcess process,
        RunState state,
        ProcessTerminationReason reason,
        bool exitedNaturally,
        BoundedOutputBuffer stdout,
        BoundedOutputBuffer stderr,
        DateTimeOffset startedAtUtc)
    {
        var rawOut = stdout.ToText();
        var rawErr = stderr.ToText();
        var normalizedOut = AnsiNormalizer.Normalize(rawOut);
        var normalizedErr = AnsiNormalizer.Normalize(rawErr);

        // The native child reports these directly: the exit code is null until the process
        // object is actually signalled, and the id is fixed from creation, so neither can
        // throw the way the managed Process wrapper used to once a run had ended.
        var exitCode = exitedNaturally ? process.ExitCode : null;
        int? processId = process.Id;

        return new ProcessRunResult
        {
            TerminationReason = reason,
            ExitCode = exitCode,
            ProcessId = processId,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = DateTimeOffset.UtcNow,
            RawStdout = rawOut,
            RawStderr = rawErr,
            NormalizedStdout = normalizedOut,
            NormalizedStderr = normalizedErr,
            AnsiNormalizationApplied = !string.Equals(rawOut, normalizedOut, StringComparison.Ordinal)
                                       || !string.Equals(rawErr, normalizedErr, StringComparison.Ordinal),
            StdoutTruncated = stdout.Truncated,
            StderrTruncated = stderr.Truncated,
            StdoutCharactersObserved = stdout.ObservedCharacters,
            StderrCharactersObserved = stderr.ObservedCharacters,
            JobTerminated = state.JobWasTerminated,
        };
    }

    /// <summary>Deadline bookkeeping for one run. The idle clock is concurrency safe.</summary>
    private sealed class RunState(ProcessRequest request)
    {
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private readonly ProcessRequest _request = request;
        private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
        private int _stdinClosed;

        public TaskCompletionSource StdinClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool JobWasTerminated { get; set; }

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startTimestamp);

        public bool HardElapsed => Elapsed >= _request.HardTimeout;

        public bool SoftElapsed => _request.SoftTimeout is { } soft && Elapsed >= soft;

        public bool IdleElapsed =>
            _request.IdleTimeout is { } idle
            && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivityTimestamp)) >= idle;

        /// <summary>Grace remaining before the hard ceiling, never beyond it.</summary>
        public TimeSpan RemainingGrace
        {
            get
            {
                var untilHard = _request.HardTimeout - Elapsed;
                if (untilHard <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }

                var grace = _request.GracefulShutdownPeriod;
                return grace < untilHard ? grace : untilHard;
            }
        }

        public void RecordActivity() => Volatile.Write(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());

        public void CloseStdin(NativeChildProcess process)
        {
            if (Interlocked.Exchange(ref _stdinClosed, 1) != 0)
            {
                return;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed by the child.
            }

            StdinClosed.TrySetResult();
        }
    }
}
