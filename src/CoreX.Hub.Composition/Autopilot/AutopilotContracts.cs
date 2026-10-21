using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Composition.Autopilot;

/// <summary>
/// Starting one execution, exactly the way the operator's Run does (ADR-0028).
///
/// A port rather than a call into the launcher because the launcher lives in the host that
/// owns the work; the autopilot decides <em>which</em> task, never <em>how</em> it runs.
/// Throws when the start is refused - the same refusals a person pressing Run would see.
/// </summary>
public interface IAutopilotRunStarter
{
    /// <returns>The execution that now exists.</returns>
    Task<Guid> StartAsync(string taskKey, string organizationSlug, CancellationToken ct);
}

/// <summary>
/// Whether a repository's default branch already contains a commit. Read-only.
///
/// Null is "could not tell", which the autopilot treats as a reason to stop, never as "no"
/// and never as "yes".
/// </summary>
public interface IBranchLineage
{
    Task<bool?> ContainsAsync(
        Organization organization,
        Repository repository,
        string commitSha,
        CancellationToken ct);
}

public enum AutopilotRepositoryState
{
    /// <summary>The repository names an autopilot block that switches nothing on.</summary>
    Off,

    /// <summary>The configuration names a repository the catalogue does not have.</summary>
    Unavailable,

    /// <summary>Nothing is queued and nothing needs anyone.</summary>
    Idle,

    /// <summary>One of this repository's tasks is running.</summary>
    Working,

    /// <summary>Work is queued but the machine's single execution slot is taken elsewhere.</summary>
    Waiting,

    /// <summary>Something needs a person before the next task may start.</summary>
    Paused,
}

public enum AutopilotAttentionKind
{
    /// <summary>A passing execution the policy did not accept; a person decides it.</summary>
    WaitingForPerson,

    /// <summary>The latest attempt did not pass. The autopilot never retries.</summary>
    AttemptDidNotPass,

    /// <summary>Accepted work that is not on the default branch yet; the next task needs it there.</summary>
    NotOnDefaultBranch,

    /// <summary>Whether accepted work is on the default branch could not be established.</summary>
    LineageUnknown,

    /// <summary>The last start the autopilot asked for was refused.</summary>
    StartRefused,
}

/// <summary>One thing that stops a repository's queue, said in terms a person can act on.</summary>
public sealed record AutopilotAttention(
    string TaskKey,
    AutopilotAttentionKind Kind,
    string Detail,
    string? Hint = null);

public sealed record AutopilotRepositoryStatus
{
    public required string OrganizationSlug { get; init; }

    public required string ProjectSlug { get; init; }

    public required string RepositorySlug { get; init; }

    public required bool RunQueue { get; init; }

    public required bool AcceptWhenClean { get; init; }

    public required AutopilotRepositoryState State { get; init; }

    public required string Detail { get; init; }

    /// <summary>Ready tasks never attempted, in the order the queue will start them.</summary>
    public IReadOnlyList<string> Queue { get; init; } = [];

    public IReadOnlyList<AutopilotAttention> Attention { get; init; } = [];
}

/// <summary>What the autopilot saw and did on its last pass.</summary>
public sealed record AutopilotSnapshot
{
    public required DateTimeOffset AtUtc { get; init; }

    public IReadOnlyList<AutopilotRepositoryStatus> Repositories { get; init; } = [];

    /// <summary>What this pass changed: an acceptance, a start. Empty on a quiet pass.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Why the pass could not run at all - an unreadable configuration, for instance.</summary>
    public string? Problem { get; init; }

    public static AutopilotSnapshot NotYetRun { get; } = new() { AtUtc = DateTimeOffset.MinValue };
}
