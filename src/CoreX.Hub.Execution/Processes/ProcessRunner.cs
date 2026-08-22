using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
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
/// Accepted residual risk (frozen architecture F.5 #1): there is a small window between
/// Process.Start and AssignProcessToJobObject. A descendant created inside that window
/// could escape the job. Phase 10 closes it with suspended process creation; Phase 2
/// deliberately does not pull that work forward.
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

    public ProcessRunner(FileSystemGuard guard) =>
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));

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

        return await ExecuteAsync(request, executable!, workingDirectory.CanonicalPath!, startedAtUtc, cancellationToken);
    }

    private static bool TryResolveExecutable(string candidate, out string? executable, out string? failure)
    {
        executable = null;
        failure = null;

        // E.1 #3 runtime boundary. Classification happens first and is purely lexical, so
        // a shell is refused before Process.Start and regardless of whether it is even
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
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var stdout = new BoundedOutputBuffer(request.MaxCapturedCharactersPerStream);
        var stderr = new BoundedOutputBuffer(request.MaxCapturedCharactersPerStream);

        using var job = JobObjectHandle.CreateKillOnClose();
        using var process = new Process { StartInfo = BuildStartInfo(request, executable, workingDirectory) };

        try
        {
            if (!process.Start())
            {
                return StartFailure(startedAtUtc, $"Windows refused to start '{executable}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return StartFailure(startedAtUtc, $"Failed to start '{executable}': {ex.Message}");
        }

        // Accepted Phase 2 race: a descendant created between Start and this assignment
        // could escape the job. Phase 10 replaces this with suspended creation.
        if (!job.TryAssign(process.SafeHandle, out var assignError) && !process.HasExited)
        {
            job.Terminate(1);
            return StartFailure(
                startedAtUtc,
                $"AssignProcessToJobObject failed with Win32 error {assignError}; the process tree could not be bounded.");
        }

        var state = new RunState(request);
        var stdoutPump = PumpAsync(process.StandardOutput, stdout, state);
        var stderrPump = PumpAsync(process.StandardError, stderr, state);
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

    private static ProcessStartInfo BuildStartInfo(
        ProcessRequest request,
        string executable,
        string workingDirectory)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,

            // ADR-0006: no shell, ever.
            UseShellExecute = false,
            CreateNoWindow = true,

            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            StandardInputEncoding = utf8,
        };

        foreach (var argument in request.Arguments)
        {
            // ArgumentList quotes each element; no command line is ever assembled.
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo, request);
        return startInfo;
    }

    /// <summary>
    /// ADR-0007: the child environment starts empty and receives only what was explicitly
    /// allowed, then the forced Hub values which cannot be overridden.
    /// </summary>
    private static void ApplyEnvironment(ProcessStartInfo startInfo, ProcessRequest request)
    {
        startInfo.Environment.Clear();

        foreach (var name in request.EnvironmentAllowlist.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var entry in request.AdditionalEnvironment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        foreach (var forced in ForcedEnvironment)
        {
            startInfo.Environment[forced.Key] = forced.Value;
        }
    }

    /// <summary>
    /// Reads one stream to end. Both pumps run concurrently from process start; reading
    /// one stream to completion before the other is the classic redirected-pipe deadlock.
    /// The loop keeps consuming the pipe even after the capture ceiling is reached.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, BoundedOutputBuffer buffer, RunState state)
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
        }
    }

    private static async Task FeedStdinAsync(Process process, ProcessRequest request, RunState state)
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

            // Stdin stays open so a child can wait on it; graceful shutdown closes it.
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
        Process process,
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

    private static async Task EndRunAsync(Process process, JobObjectHandle job, RunState state, bool graceful)
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

    private static async Task WaitForExitQuietlyAsync(Process process, TimeSpan timeout)
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
        Process process,
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

        int? exitCode = null;
        if (exitedNaturally)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = null;
            }
        }

        int? processId = null;
        try
        {
            processId = process.Id;
        }
        catch (InvalidOperationException)
        {
            processId = null;
        }

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

        public void CloseStdin(Process process)
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
