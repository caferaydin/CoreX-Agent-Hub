using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// One attempt at a task (architecture D.3).
///
/// Phase 1 establishes the persisted shape only. The pipeline that drives state,
/// gates, commits and evidence is Phase 5 and is intentionally absent here.
/// </summary>
public sealed class Execution
{
    private Execution()
    {
        // EF Core
    }

    public Execution(
        Guid id,
        Guid organizationId,
        Guid taskId,
        int attemptNo,
        string baseCommitSha,
        string branchName,
        string worktreePath,
        Guid? previousExecutionId,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        TaskId = Guard.NotEmpty(taskId, nameof(taskId));
        AttemptNo = Guard.Positive(attemptNo, nameof(attemptNo));
        BaseCommitSha = NormalizeSha(baseCommitSha, nameof(baseCommitSha));
        BranchName = Guard.NotBlankMax(branchName, nameof(branchName), 200);
        WorktreePath = Guard.NotBlankMax(worktreePath, nameof(worktreePath), 400);
        PreviousExecutionId = previousExecutionId;
        State = ExecutionState.Created;
        WorkspaceState = WorkspaceState.Active;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid TaskId { get; private set; }

    public int AttemptNo { get; private set; }

    /// <summary>ADR-0003: the pinned commit the worktree was created from.</summary>
    public string BaseCommitSha { get; private set; } = null!;

    public string BranchName { get; private set; } = null!;

    public string WorktreePath { get; private set; } = null!;

    public string? ContextPackHash { get; private set; }

    public ExecutionState State { get; private set; }

    /// <summary>Set only when <see cref="State"/> is <see cref="ExecutionState.Completed"/>.</summary>
    public ExecutionOutcome? Outcome { get; private set; }

    /// <summary>
    /// ADR-0016: null whenever a gate blocked the pipeline. A null result commit also means
    /// the worktree may not be removed automatically.
    /// </summary>
    public string? ResultCommitSha { get; private set; }

    /// <summary>Errata E1: recorded whenever HEAD moved away from the base commit.</summary>
    public bool UnexpectedAgentCommitDetected { get; private set; }

    /// <summary>Newline separated SHAs of commits the agent created (errata E1).</summary>
    public string? UnexpectedCommitShas { get; private set; }

    public WorkspaceState WorkspaceState { get; private set; }

    public Guid? PreviousExecutionId { get; private set; }

    /// <summary>Owning host process id, used for crash detection (architecture P.6).</summary>
    public int? LeaseProcessId { get; private set; }

    public DateTimeOffset? LeaseHostStartedAtUtc { get; private set; }

    public DateTimeOffset? LeaseHeartbeatUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsTerminal => ExecutionStateRules.IsTerminal(State);

    /// <summary>ADR-0016 invariant: no result commit means the worktree still holds the work.</summary>
    public bool WorktreeMustBePreserved => ResultCommitSha is null;

    private static string NormalizeSha(string? value, string name)
    {
        var sha = Guard.NotBlankMax(value, name, 40);
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit))
        {
            throw new DomainException($"{name} must be a full 40 character hexadecimal git object id.");
        }

        return sha.ToLowerInvariant();
    }
}
