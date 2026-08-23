using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// One attempt at a task (architecture D.3).
///
/// Phase 1 establishes the persisted shape only. The pipeline that drives state,
/// gates, commits and evidence is Phase 5 and is intentionally absent here.
/// </summary>
public sealed partial class Execution
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

    /// <summary>
    /// ADR-0003: the pinned commit the worktree was created from.
    ///
    /// Provenance, not workspace bookkeeping. It is resolved before this row exists and
    /// never changes afterwards, because the baseline cache, the context pack and every
    /// captured diff are all keyed on it: a value that could be rewritten later would make
    /// all three claim to describe a commit they never measured.
    /// </summary>
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

    /// <summary>
    /// The one place an execution decides whether a string is a commit id.
    ///
    /// The frozen model lists <c>CommitSha</c> as a value object and it is not a type yet;
    /// until it is, this method is that concept's only implementation, and no second parser
    /// exists beside it.
    ///
    /// Shape alone is not the invariant. The all zero object id is git's "no object" value
    /// and is exactly what an orchestrator reaches for when it wants a row to exist before
    /// it has resolved a commit, so it is refused here: what an execution records has to be
    /// a commit the repository actually answered with, not a placeholder standing in for
    /// one.
    /// </summary>
    private static string NormalizeSha(string? value, string name)
    {
        var sha = Guard.NotBlankMax(value, name, 40);
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit))
        {
            throw new DomainException($"{name} must be a full 40 character hexadecimal git object id.");
        }

        if (sha.All(c => c == '0'))
        {
            throw new DomainException(
                $"{name} must be a commit resolved from the repository; the null object id names none.");
        }

        return sha.ToLowerInvariant();
    }
}
