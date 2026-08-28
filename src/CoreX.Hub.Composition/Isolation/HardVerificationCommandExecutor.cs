using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Runs a hard organization's verification commands inside the boundary, not beside it.
///
/// This is the adapter the phase exists for. Verification is where repository-controlled code is
/// most obviously executed - a test suite is a program the repository wrote - and until now a hard
/// organization's tests ran through the Windows executor, as the developer, on the developer's
/// filesystem. An agent perfectly contained for its own turn could still put anything it liked in
/// a test file and have the Hub run it minutes later with every boundary switched off.
///
/// So a hard verification command is an untrusted hard workload like any other: the same Linux
/// native workspace, the same non-root account, the same network namespace and the same mount
/// sandbox, launched through the same <see cref="HardWorkloadLaunch"/> policy the agent uses. If
/// this ran anywhere else the isolation would be theatre, which is why the placement is a
/// constructor argument rather than something a command can carry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardVerificationCommandExecutor : IVerificationCommandExecutor
{
    private readonly HardExecutionEnvironment _environment;
    private readonly HardIsolationProfile _profile;
    private readonly string _workspacePath;
    private readonly Guid _correlationId;

    /// <summary>
    /// Bound to the execution's environment rather than to an executor.
    ///
    /// The environment hands out one lease for the whole execution, so a verification command is
    /// placed in the same namespace the agent ran in. Taking a ready-made executor instead would
    /// let a caller build this against a different one, which is the mistake that produces an
    /// isolated agent and a verifier somewhere else.
    /// </summary>
    public HardVerificationCommandExecutor(
        HardExecutionEnvironment environment,
        HardIsolationProfile profile,
        string workspacePath,
        Guid correlationId)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _workspacePath = workspacePath ?? throw new ArgumentNullException(nameof(workspacePath));
        _correlationId = correlationId;
    }

    public async Task<VerificationCommandResult> ExecuteAsync(
        VerificationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var lease = await _environment.LeaseAsync(cancellationToken);

        if (lease is null)
        {
            // No boundary, no verification. A repository's own test suite is the most obviously
            // repository-controlled thing in an execution, and running it on the host because the
            // namespace could not be established is the failure this adapter exists to prevent.
            return new VerificationCommandResult
            {
                TerminationReason = VerificationTerminationReason.StartFailed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                StartFailureMessage =
                    "the hard isolation boundary for this execution is not available: "
                    + (_environment.Refusal?.ToString() ?? "unknown"),
            };
        }

        var request = new HardProcessRequest
        {
            Executable = command.Executable,
            Arguments = command.Arguments,

            // Always the hard workspace, never the working directory a command asked for. A
            // verification profile is repository adjacent configuration, and a path from it is not
            // a place this is willing to be sent.
            WorkingDirectory = _workspacePath,
            Environment = command.Environment,
            HardTimeout = command.Timeout,
            CorrelationId = _correlationId,
        };

        var result = await lease.Executor.RunAsync(_profile, request, cancellationToken);

        return new VerificationCommandResult
        {
            // Mapped exhaustively rather than by a default, so a new hard outcome has to be given
            // a meaning here instead of quietly arriving as a passing command.
            TerminationReason = result.Outcome switch
            {
                HardProcessOutcome.Exited => VerificationTerminationReason.Exited,
                HardProcessOutcome.Cancelled => VerificationTerminationReason.Cancelled,
                HardProcessOutcome.SoftTimeout => VerificationTerminationReason.TimedOut,
                HardProcessOutcome.IdleTimeout => VerificationTerminationReason.TimedOut,
                HardProcessOutcome.HardTimeout => VerificationTerminationReason.TimedOut,
                _ => VerificationTerminationReason.StartFailed,
            },
            ExitCode = result.ExitCode,
            StartedAtUtc = result.StartedAtUtc,
            EndedAtUtc = result.EndedAtUtc,
            Stdout = result.Stdout,
            Stderr = result.Stderr,
            StdoutTruncated = result.StdoutTruncated,
            StderrTruncated = result.StderrTruncated,

            // A workload that never started is reported as exactly that rather than as a failing
            // command: a refused launch means the boundary could not be established, and treating
            // it as a test failure would turn an isolation problem into a code review problem.
            StartFailureMessage = result.Outcome is HardProcessOutcome.StartFailed
                ? result.StartFailureMessage
                : null,
        };
    }
}
