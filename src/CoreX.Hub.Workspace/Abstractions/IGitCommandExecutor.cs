namespace CoreX.Hub.Workspace.Abstractions;

/// <summary>
/// One git invocation. An executable plus an argument vector: there is no command line
/// and no shell (ADR-0006). The environment entries are Hub owned fixed values layered on
/// top of whatever the executor's allowlist provides; they never mutate the parent.
/// </summary>
public sealed record GitCommand
{
    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public TimeSpan? Timeout { get; init; }

    /// <summary>Short label used in evidence and failure messages.</summary>
    public string Describe() => "git " + string.Join(' ', Arguments);
}

/// <summary>
/// Machine truth for one git invocation. Success is the exit code, never the text
/// (Phase 3 brief section 31).
/// </summary>
public sealed record GitCommandResult
{
    public required bool Started { get; init; }

    public int? ExitCode { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public string? FailureMessage { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Succeeded => Started && ExitCode == 0;

    public string Describe() =>
        Started
            ? $"exit {ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}: {Stderr.Trim()}"
            : $"not started: {FailureMessage}";
}

/// <summary>
/// The Workspace owned port for running git. The production adapter lives in the
/// composition layer and delegates to ProcessRunner, so every operating system process
/// still comes from the single Phase 2 launch point (E.1 #1, #4).
/// </summary>
public interface IGitCommandExecutor
{
    Task<GitCommandResult> ExecuteAsync(GitCommand command, CancellationToken cancellationToken);
}
