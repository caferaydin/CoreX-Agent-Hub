namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// Machine truth for one process run (architecture I.1 tier A).
///
/// It is intentionally NOT an execution outcome. Mapping a termination reason onto
/// <c>Execution.Outcome</c> belongs to the orchestration phases.
/// </summary>
public sealed record ProcessRunResult
{
    public required ProcessTerminationReason TerminationReason { get; init; }

    /// <summary>Null when the process never started or was killed before exiting cleanly.</summary>
    public int? ExitCode { get; init; }

    public int? ProcessId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    /// <summary>Exactly what the process wrote, escape sequences included.</summary>
    public string RawStdout { get; init; } = string.Empty;

    public string RawStderr { get; init; } = string.Empty;

    /// <summary>Readable evidence: the same text with terminal control sequences removed.</summary>
    public string NormalizedStdout { get; init; } = string.Empty;

    public string NormalizedStderr { get; init; } = string.Empty;

    public bool AnsiNormalizationApplied { get; init; }

    public bool StdoutTruncated { get; init; }

    public bool StderrTruncated { get; init; }

    /// <summary>Characters observed on the pipe, including any beyond the capture ceiling.</summary>
    public long StdoutCharactersObserved { get; init; }

    public long StderrCharactersObserved { get; init; }

    /// <summary>True when the job object had to be terminated to end the run.</summary>
    public bool JobTerminated { get; init; }

    /// <summary>Populated only when <see cref="ProcessTerminationReason.StartFailed"/>.</summary>
    public string? StartFailureMessage { get; init; }

    public bool TimedOut => TerminationReason
        is ProcessTerminationReason.SoftTimeout
        or ProcessTerminationReason.IdleTimeout
        or ProcessTerminationReason.HardTimeout;

    public bool Cancelled => TerminationReason == ProcessTerminationReason.Cancelled;

    public bool Started => TerminationReason != ProcessTerminationReason.StartFailed;
}
