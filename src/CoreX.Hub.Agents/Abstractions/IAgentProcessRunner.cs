namespace CoreX.Hub.Agents.Abstractions;

/// <summary>Why one agent child process ended. Mirrors the Phase 2 machine truth.</summary>
public enum AgentProcessTermination
{
    Exited,
    StartFailed,
    Cancelled,
    TimedOut,
}

/// <summary>
/// One child process: an absolute executable plus an argument vector (ADR-0006). There is
/// no command line and no shell.
/// </summary>
public sealed record AgentProcessRequest
{
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    public IReadOnlyList<string> EnvironmentAllowlist { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan? SoftTimeout { get; init; }

    public TimeSpan? IdleTimeout { get; init; }

    public required TimeSpan HardTimeout { get; init; }

    /// <summary>
    /// Written to the child's stdin, which is then closed.
    ///
    /// It exists so a runtime can hand a tool its prompt without putting it on the operating
    /// system command line, where every process on the machine can read it. Null leaves the
    /// existing behaviour untouched: stdin stays open until the run ends.
    /// </summary>
    public string? StandardInputText { get; init; }

    /// <summary>
    /// Called with each chunk of stdout as it is read, before anything is done with it.
    ///
    /// This is what makes incremental observation possible at all: the result is only
    /// available once the child has exited, so a runtime that must report progress while the
    /// run is still going has nowhere else to look. It sees the bytes as they arrive, so it
    /// is not bounded by the capture ceiling either.
    ///
    /// Calls are sequential and arrive on the pump, so an observer that blocks stops the
    /// pipe being drained. It must be cheap, and it must not throw.
    /// </summary>
    public Action<string>? StandardOutputObserver { get; init; }
}

public sealed record AgentProcessResult
{
    public required AgentProcessTermination Termination { get; init; }

    public int? ExitCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public string? StartFailureMessage { get; init; }
}

/// <summary>
/// The Agents owned port for running a child process.
///
/// A runtime that drives an external tool needs to start it; E.1 #1 says there is exactly
/// one operating system launch point and E.1 #4 says this module may not reference the
/// execution module. Both hold: the runtime asks through this port and the composition
/// layer answers with ProcessRunner.
/// </summary>
public interface IAgentProcessRunner
{
    Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken);
}
