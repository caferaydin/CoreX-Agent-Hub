namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// How one contained run ended.
///
/// The same six answers the Windows process layer gives, restated here because the Application
/// layer does not depend on an adapter - and deliberately not a seventh. It was tempting to add
/// one for "the environment's own runtime ceiling ended it", since that ceiling is the thing
/// that makes a hard run bounded even when the Hub is gone. It is not here because on this path
/// it cannot be established: systemd-run returns the same status when its runtime ceiling fires
/// as when the workload exits non-zero (measured, systemd 259), and the only way to tell them
/// apart is to leave the unit loaded and interrogate it afterwards - which gives up the
/// self-collection that stops an abandoned Hub leaving failed units behind.
///
/// So the ceiling is proven by test rather than announced by a value nothing can produce. See
/// the Linux runtime ceiling notes on <see cref="HardProcessRequest.HardTimeout"/>.
/// </summary>
public enum HardProcessOutcome
{
    /// <summary>The workload ran to completion; <c>ExitCode</c> is authoritative.</summary>
    Exited,

    /// <summary>Nothing ran. A precondition failed, or the environment refused to start it.</summary>
    StartFailed,

    /// <summary>The caller cancelled, and the whole execution cgroup was terminated.</summary>
    Cancelled,

    /// <summary>The graceful deadline elapsed.</summary>
    SoftTimeout,

    /// <summary>Neither output stream produced anything for the configured idle interval.</summary>
    IdleTimeout,

    /// <summary>The absolute deadline elapsed.</summary>
    HardTimeout,
}

/// <summary>
/// What to run inside a hard isolated environment.
///
/// The same shape as any other process request in this system - an executable, an argument
/// vector, a working directory, an environment, three deadlines - and deliberately nothing
/// more. There is no provider here, no task, no repository, no reviewer and no acceptance: this
/// is the process boundary, and everything above it is somebody else's composition.
///
/// The one field that is not about the process itself is <see cref="CorrelationId"/>, and it is
/// here because the environment needs a name for the unit it creates. That name has to come
/// from trusted Hub identity rather than from anything the workload can influence.
/// </summary>
public sealed record HardProcessRequest
{
    public required string Executable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>An absolute path inside the environment, beneath the profile's execution root.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// The workload's entire environment, stated explicitly.
    ///
    /// Not an allowlist over the host environment, which is the crucial difference from the
    /// Windows side: nothing of the Windows caller is inherited, offered or forwarded, so there
    /// is no list of names to get wrong. What the workload gets is whatever the environment
    /// gives a service of that Linux user, plus exactly these.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Written to the workload's standard input, followed by end of stream.</summary>
    public string? StdinPayload { get; init; }

    /// <summary>Holds standard input open rather than ending it immediately.</summary>
    public bool KeepStandardInputOpen { get; init; }

    public TimeSpan? SoftTimeout { get; init; }

    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// The absolute ceiling, enforced on both sides of the boundary.
    ///
    /// Two ceilings, deliberately, and they are not redundant. The host supervisor holds this
    /// one; the environment is given its own, slightly longer, so that whenever the Hub is alive
    /// the host notices first and stops the unit itself. The Linux ceiling exists for the case
    /// the host cannot cover: killing the Windows process that launched a Linux service proves
    /// nothing whatever about the service, so the environment has to be able to end the work on
    /// its own, with nobody watching.
    /// </summary>
    public required TimeSpan HardTimeout { get; init; }

    /// <summary>Trusted Hub identity for this run. Names the transient unit; never workload input.</summary>
    public required Guid CorrelationId { get; init; }

    public int MaxCapturedCharactersPerStream { get; init; } = 8 * 1024 * 1024;
}

/// <summary>Machine truth for one contained run. Not an execution outcome.</summary>
public sealed record HardProcessResult
{
    public required HardProcessOutcome Outcome { get; init; }

    /// <summary>Null unless the workload ran to completion.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The transient unit this run owned. Empty when nothing was ever created.</summary>
    public string UnitName { get; init; } = string.Empty;

    /// <summary>
    /// The runtime ceiling the environment itself was given, in seconds.
    ///
    /// Reported so the two deadlines are visible together. It is always longer than the host's,
    /// and it is the one that still applies if the Hub disappears.
    /// </summary>
    public long EnvironmentRuntimeCeilingSeconds { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public bool StdoutTruncated { get; init; }

    public bool StderrTruncated { get; init; }

    /// <summary>Populated only when <see cref="HardProcessOutcome.StartFailed"/>.</summary>
    public string? StartFailureMessage { get; init; }

    public bool Started => Outcome != HardProcessOutcome.StartFailed;

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;
}

/// <summary>
/// Runs one process inside a hard isolated environment, contained.
///
/// A port, for the same reason the readiness probe is one: the mechanism is entirely operating
/// system specific and nothing above this line may know it. An implementation is responsible for
/// the whole containment story - the workload's identity, its working directory, its
/// environment, its process and memory ceilings, its runtime ceiling, and ending its entire
/// process tree rather than one process it happens to know the id of.
///
/// It is emphatically not an execution plane. Running a process inside the environment is one
/// part of running a Hub task there, and the rest - the repository, the context, git,
/// verification, review, evidence, provider authentication, control plane isolation - is still
/// on the host. <see cref="IHardExecutionPlane"/> answers the question about the whole workflow
/// and continues to answer no.
/// </summary>
public interface IHardProcessExecutor
{
    /// <summary>
    /// Runs the request inside <paramref name="profile"/>'s environment.
    ///
    /// Fail closed throughout: a precondition that cannot be established, a ceiling that cannot
    /// be applied, or an identity that cannot be resolved means no workload runs at all, and
    /// never means a workload running with weaker containment.
    /// </summary>
    Task<HardProcessResult> RunAsync(
        HardIsolationProfile profile,
        HardProcessRequest request,
        CancellationToken ct);
}
