namespace CoreX.Hub.Execution.Processes;

/// <summary>Why a process run ended. Deliberately not mapped to any orchestration outcome.</summary>
public enum ProcessTerminationReason
{
    /// <summary>The process exited on its own; <c>ExitCode</c> is authoritative.</summary>
    Exited,

    /// <summary>The process could not be started at all.</summary>
    StartFailed,

    /// <summary>The caller cancelled the run.</summary>
    Cancelled,

    /// <summary>The soft deadline elapsed and graceful shutdown was requested.</summary>
    SoftTimeout,

    /// <summary>Neither stream produced activity for the configured idle interval.</summary>
    IdleTimeout,

    /// <summary>The absolute wall clock ceiling was reached.</summary>
    HardTimeout,
}

/// <summary>Raised when a caller supplies a request that can never be valid.</summary>
public sealed class ProcessRequestException : Exception
{
    public ProcessRequestException(string message) : base(message)
    {
    }
}

/// <summary>
/// What to run. A low level primitive: it knows nothing about tasks, executions, agents,
/// reviews or git.
///
/// ADR-0006: an executable plus an argument vector. There is no command line to escape
/// and no shell to interpret it.
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>Default ceiling per stream. Bounds memory, never pipe consumption.</summary>
    public const int DefaultMaxCapturedCharactersPerStream = 8 * 1024 * 1024;

    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Must resolve inside the runner's trust root before the child starts.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// ADR-0007: names copied from the parent environment. Everything not listed here is
    /// invisible to the child, because the child environment starts empty.
    /// </summary>
    public IReadOnlyList<string> EnvironmentAllowlist { get; init; } = [];

    /// <summary>Explicit values set regardless of the parent environment.</summary>
    public IReadOnlyDictionary<string, string> AdditionalEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Graceful deadline. Null disables it.</summary>
    public TimeSpan? SoftTimeout { get; init; }

    /// <summary>No stdout or stderr activity for this long ends the run. Null disables it.</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Absolute ceiling. Always enforced and never postponed by output.</summary>
    public required TimeSpan HardTimeout { get; init; }

    /// <summary>How long a graceful attempt is given before the job is terminated.</summary>
    public TimeSpan GracefulShutdownPeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Written to stdin and followed by end of stream. When null, the child is given end of
    /// stream immediately unless <see cref="KeepStandardInputOpen"/> says otherwise.
    /// </summary>
    public string? StdinPayload { get; init; }

    /// <summary>
    /// Holds the child's stdin open until graceful shutdown instead of ending it at once.
    ///
    /// Off by default, and that default was measured rather than assumed. Stdin is always
    /// redirected here, so a child that is handed a pipe nobody ever closes can wait on it
    /// for ever: `dotnet test` over this solution finished every one of its projects and
    /// then sat there until the hard deadline killed it - twenty minutes for six minutes of
    /// work - purely because end of stream never came. A tool that reads nothing is not
    /// harmed by being told there is nothing to read, so the safe default is to say so.
    ///
    /// Set this only for a child that genuinely waits on stdin and is meant to keep running
    /// until the Hub asks it to stop.
    /// </summary>
    public bool KeepStandardInputOpen { get; init; }

    /// <summary>
    /// Called with each chunk of stdout as it is read, before it is captured.
    ///
    /// The captured result exists only once the child has exited, so a caller that has to
    /// observe a long run while it is still going has nowhere else to look. The observer
    /// also sees everything, including output past
    /// <see cref="MaxCapturedCharactersPerStream"/>, because the pump keeps consuming the
    /// pipe after the capture ceiling is reached.
    ///
    /// Calls are sequential and happen on the stdout pump. An observer that blocks stops the
    /// pipe being drained and will eventually stall the child, so it must be cheap. An
    /// observer that throws is ignored: a broken observer may not end a run.
    /// </summary>
    public Action<string>? StandardOutputObserver { get; init; }

    public int MaxCapturedCharactersPerStream { get; init; } = DefaultMaxCapturedCharactersPerStream;

    /// <summary>Fails fast on a request that could never behave sensibly.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable))
        {
            throw new ProcessRequestException("Executable must not be empty.");
        }

        if (Arguments.Any(a => a is null))
        {
            throw new ProcessRequestException("Arguments must not contain null entries.");
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            throw new ProcessRequestException("WorkingDirectory must not be empty.");
        }

        if (HardTimeout <= TimeSpan.Zero)
        {
            throw new ProcessRequestException("HardTimeout must be greater than zero.");
        }

        if (GracefulShutdownPeriod < TimeSpan.Zero)
        {
            throw new ProcessRequestException("GracefulShutdownPeriod must not be negative.");
        }

        if (MaxCapturedCharactersPerStream <= 0)
        {
            throw new ProcessRequestException("MaxCapturedCharactersPerStream must be greater than zero.");
        }

        ValidateSubDeadline(SoftTimeout, nameof(SoftTimeout));
        ValidateSubDeadline(IdleTimeout, nameof(IdleTimeout));

        foreach (var name in EnvironmentAllowlist)
        {
            ValidateEnvironmentName(name, nameof(EnvironmentAllowlist));
        }

        foreach (var entry in AdditionalEnvironment)
        {
            ValidateEnvironmentName(entry.Key, nameof(AdditionalEnvironment));

            if (entry.Value is null || entry.Value.Contains('\0'))
            {
                throw new ProcessRequestException(
                    $"AdditionalEnvironment value for '{entry.Key}' is not representable as an environment value.");
            }
        }
    }

    private static void ValidateEnvironmentName(string? name, string source)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProcessRequestException($"{source} contains an empty environment variable name.");
        }

        if (name.Contains('=') || name.Contains('\0'))
        {
            throw new ProcessRequestException(
                $"{source} entry '{name}' is not a representable environment variable name.");
        }
    }

    private void ValidateSubDeadline(TimeSpan? value, string name)
    {
        if (value is not { } deadline)
        {
            return;
        }

        if (deadline <= TimeSpan.Zero)
        {
            throw new ProcessRequestException($"{name} must be greater than zero when set.");
        }

        if (deadline >= HardTimeout)
        {
            throw new ProcessRequestException(
                $"{name} ({deadline}) must be shorter than HardTimeout ({HardTimeout}); the hard deadline is the authoritative maximum runtime.");
        }
    }
}
