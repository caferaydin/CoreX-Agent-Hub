namespace CoreX.Hub.Verification.Abstractions;

/// <summary>Why one verification command ended. Mirrors the Phase 2 machine truth.</summary>
public enum VerificationTerminationReason
{
    Exited,
    StartFailed,
    Cancelled,
    TimedOut,
}

/// <summary>
/// One verification command: an absolute executable plus an argument vector
/// (ADR-0006). There is no command line, so there is nothing for a shell to interpret -
/// and no shell is involved at any point.
/// </summary>
public sealed record VerificationCommand
{
    /// <summary>Already resolved through the executable allowlist; always absolute.</summary>
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Names copied from the parent environment. Everything else is invisible to the
    /// child because the child environment starts empty (ADR-0007).
    /// </summary>
    public IReadOnlyList<string> EnvironmentAllowlist { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The absolute ceiling for this step; the profile timeout, never a second one.</summary>
    public required TimeSpan Timeout { get; init; }
}

/// <summary>Machine truth for one verification command. Success is the exit code, never the text.</summary>
public sealed record VerificationCommandResult
{
    public required VerificationTerminationReason TerminationReason { get; init; }

    public int? ExitCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public bool StdoutTruncated { get; init; }

    public bool StderrTruncated { get; init; }

    /// <summary>Populated only when the process never started.</summary>
    public string? StartFailureMessage { get; init; }
}

/// <summary>
/// The Verification owned port for running a process. The production adapter lives in the
/// composition layer and delegates to ProcessRunner, so there is still exactly one
/// operating system process launch point (E.1 #1, E.1 #4).
/// </summary>
public interface IVerificationCommandExecutor
{
    Task<VerificationCommandResult> ExecuteAsync(
        VerificationCommand command,
        CancellationToken cancellationToken);
}
