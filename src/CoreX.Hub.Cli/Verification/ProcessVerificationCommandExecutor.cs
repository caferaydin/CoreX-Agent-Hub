using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Cli.Verification;

/// <summary>
/// Composition layer adapter: the verification module asks for a command, and it is run
/// through ProcessRunner like every other process in the system.
///
/// Two accepted invariants hold at the same time. E.1 #1: there is still a single
/// operating system process launch point, with its job object, three level timeouts,
/// bounded capture and runtime no-shell policy. E.1 #4: the verification module does not
/// reference the execution module, and the execution module does not reference it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessVerificationCommandExecutor : IVerificationCommandExecutor
{
    private readonly FileSystemGuard _guard;
    private readonly ProcessRunner _runner;

    public ProcessVerificationCommandExecutor(string trustRoot)
    {
        _guard = FileSystemGuard.Create(trustRoot);
        _runner = new ProcessRunner(_guard);
    }

    public string TrustRoot => _guard.TrustRoot;

    public async Task<VerificationCommandResult> ExecuteAsync(
        VerificationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startedAt = DateTimeOffset.UtcNow;

        ProcessRunResult result;
        try
        {
            var request = new ProcessRequest
            {
                Executable = command.Executable,
                Arguments = command.Arguments,
                WorkingDirectory = command.WorkingDirectory,
                EnvironmentAllowlist = command.EnvironmentAllowlist,
                AdditionalEnvironment = command.Environment,
                HardTimeout = command.Timeout,
            };

            result = await _runner.RunAsync(request, cancellationToken);
        }
        catch (ProcessRequestException ex)
        {
            // A request the runner refuses outright - a shell executable, for example -
            // never becomes a process.
            return new VerificationCommandResult
            {
                TerminationReason = VerificationTerminationReason.StartFailed,
                StartedAtUtc = startedAt,
                EndedAtUtc = DateTimeOffset.UtcNow,
                StartFailureMessage = ex.Message,
            };
        }
        catch (PathTrustException ex)
        {
            return new VerificationCommandResult
            {
                TerminationReason = VerificationTerminationReason.StartFailed,
                StartedAtUtc = startedAt,
                EndedAtUtc = DateTimeOffset.UtcNow,
                StartFailureMessage = ex.Message,
            };
        }

        return new VerificationCommandResult
        {
            TerminationReason = Map(result.TerminationReason),
            ExitCode = result.ExitCode,
            StartedAtUtc = result.StartedAtUtc,
            EndedAtUtc = result.EndedAtUtc,
            Stdout = result.NormalizedStdout,
            Stderr = result.NormalizedStderr,
            StdoutTruncated = result.StdoutTruncated,
            StderrTruncated = result.StderrTruncated,
            StartFailureMessage = result.StartFailureMessage,
        };
    }

    private static VerificationTerminationReason Map(ProcessTerminationReason reason) => reason switch
    {
        ProcessTerminationReason.Exited => VerificationTerminationReason.Exited,
        ProcessTerminationReason.StartFailed => VerificationTerminationReason.StartFailed,
        ProcessTerminationReason.Cancelled => VerificationTerminationReason.Cancelled,
        _ => VerificationTerminationReason.TimedOut,
    };
}
