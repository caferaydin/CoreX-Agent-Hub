using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Acceptance;

/// <summary>What happened to the worktree after a decision was already recorded.</summary>
public enum WorkspaceCleanupOutcome
{
    /// <summary>This decision does not release a worktree, or no release port is wired.</summary>
    NotAttempted,

    Released,

    /// <summary>The worktree would not go away. Reported, never forced.</summary>
    Orphaned,

    /// <summary>Refused: the execution produced no commit, so its worktree holds the only copy (D6).</summary>
    DeniedNoResultCommit,

    Failed,
}

/// <summary>
/// Cleanup truth, kept separate from decision truth on purpose.
///
/// A decision is a person's word and it is already recorded by the time any of this runs.
/// Whether a directory could be removed afterwards is an operational fact about a machine,
/// and reporting the two together as one outcome would let a locked file look like a
/// refused acceptance.
/// </summary>
public sealed record WorkspaceCleanupResult(WorkspaceCleanupOutcome Outcome, string Detail)
{
    public static WorkspaceCleanupResult NotAttempted(string why) =>
        new(WorkspaceCleanupOutcome.NotAttempted, why);

    public bool Succeeded => Outcome == WorkspaceCleanupOutcome.Released;
}

/// <summary>
/// The Application owned port for releasing an execution's worktree.
///
/// Acceptance decides that a worktree may go; the workspace module decides how, and remains
/// the only thing in the system that runs git. The port is deliberately one method: there
/// is nothing to configure and nothing to force.
///
/// It is invoked only after the decision is committed, so an implementation may fail freely
/// - what it cannot do is take a recorded decision back.
/// </summary>
public interface IExecutionWorkspaceRelease
{
    Task<WorkspaceCleanupResult> ReleaseAsync(
        Guid repositoryId,
        Execution execution,
        CancellationToken ct);
}
