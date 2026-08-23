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
