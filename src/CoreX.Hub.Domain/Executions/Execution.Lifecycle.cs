using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// How an execution is allowed to move.
///
/// The aggregate owns its own legality: <see cref="ExecutionStateRules"/> says what is
/// terminal and what produces a result commit, and these methods refuse anything that
/// would contradict it. An orchestrator can sequence the pipeline but cannot invent a
/// state the model does not allow.
/// </summary>
public sealed partial class Execution
{
    /// <summary>Moves the pipeline forward. Terminal executions never move again.</summary>
    public void Advance(ExecutionState next, DateTimeOffset nowUtc)
    {
        if (IsTerminal)
        {
            throw new DomainException($"Execution {Id} is already {State} and cannot advance to {next}.");
        }

        if (ExecutionStateRules.IsTerminal(next))
        {
            throw new DomainException(
                $"{next} is terminal and must be reached through a completion method, not Advance.");
        }

        State = next;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records where the workspace was materialised, and refuses to let it rewrite what
    /// this execution is based on.
    ///
    /// The base commit is resolved before this row exists and arrives through the
    /// constructor; a prepared workspace can only ever confirm it. So the commit is an
    /// argument here in order to be checked, not in order to be stored: a workspace built
    /// from a different commit is not this execution's workspace, and adopting its answer
    /// would silently move the provenance a baseline and a context pack are keyed on. The
    /// mismatch fails closed instead.
    /// </summary>
    public void RecordWorkspace(
        string preparedBaseCommitSha,
        string branchName,
        string worktreePath,
        DateTimeOffset nowUtc)
    {
        var prepared = NormalizeSha(preparedBaseCommitSha, nameof(preparedBaseCommitSha));

        if (!string.Equals(prepared, BaseCommitSha, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Execution {Id} is based on {BaseCommitSha}, but the prepared workspace reports {prepared}. "
                + "An execution's base commit is provenance and is never moved to match a workspace.");
        }

        BranchName = Guard.NotBlankMax(branchName, nameof(branchName), 200);
        WorktreePath = Guard.NotBlankMax(worktreePath, nameof(worktreePath), 400);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records the context pack this execution ran against.</summary>
    public void RecordContextPack(string contextPackHash, DateTimeOffset nowUtc)
    {
        ContextPackHash = Guard.NotBlankMax(contextPackHash, nameof(contextPackHash), 64);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records what git observed about the agent's own commits. This is diagnosis, not a
    /// verdict: whether it decides the outcome depends on whether the agent step itself
    /// already failed (errata E1, collision M4).
    /// </summary>
    public void RecordUnexpectedCommits(IReadOnlyList<string> commitShas, DateTimeOffset nowUtc)
    {
        UnexpectedAgentCommitDetected = true;
        UnexpectedCommitShas = commitShas.Count == 0
            ? string.Empty
            : Guard.NotBlankMax(string.Join('\n', commitShas), nameof(commitShas), 4000);

        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records the Hub's own result commit. Only the workspace module creates it; this
    /// only remembers that it exists, which is also what stops the worktree from being
    /// treated as disposable.
    /// </summary>
    public void RecordResultCommit(string resultCommitSha, DateTimeOffset nowUtc)
    {
        ResultCommitSha = NormalizeSha(resultCommitSha, nameof(resultCommitSha));
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Finishes the pipeline with a verdict.
    ///
    /// The outcome and the result commit have to agree: an outcome that never produces a
    /// commit cannot carry one, and one that does cannot be missing it. Getting this wrong
    /// would let a blocked execution look committed, or a passing one look empty.
    /// </summary>
    public void Complete(ExecutionOutcome outcome, DateTimeOffset nowUtc)
    {
        if (IsTerminal)
        {
            throw new DomainException($"Execution {Id} is already {State}.");
        }

        var producesCommit = ExecutionStateRules.ProducesResultCommit(outcome);

        if (producesCommit && ResultCommitSha is null)
        {
            throw new DomainException($"Outcome {outcome} requires a result commit but none was recorded.");
        }

        if (!producesCommit && ResultCommitSha is not null)
        {
            throw new DomainException($"Outcome {outcome} must not carry a result commit.");
        }

        State = ExecutionState.Completed;
        Outcome = outcome;
        UpdatedAtUtc = nowUtc;
        ClearLease();
    }

    /// <summary>
    /// Ends the pipeline without a verdict: the agent failed, ran out of time, was
    /// cancelled, or the host died. These carry no outcome by design - failure lives on
    /// the execution and never becomes a judgement about the work.
    /// </summary>
    public void Terminate(ExecutionState terminalState, DateTimeOffset nowUtc)
    {
        if (terminalState is not (ExecutionState.Failed or ExecutionState.TimedOut
            or ExecutionState.Cancelled or ExecutionState.Interrupted))
        {
            throw new DomainException($"{terminalState} is not a terminal failure state.");
        }

        if (IsTerminal)
        {
            throw new DomainException($"Execution {Id} is already {State}.");
        }

        // A cancelled execution is defined as one that produced no result commit, so an
        // execution that already made one cannot be turned into a cancellation. Interrupted
        // deliberately stays legal: a host that died after the commit was created really
        // does hold one, and pretending otherwise would hide it. Nothing here removes,
        // rewrites or disowns a commit to make a state fit.
        if (terminalState == ExecutionState.Cancelled && ResultCommitSha is not null)
        {
            throw new DomainException(
                $"Execution {Id} already recorded result commit {ResultCommitSha}, so it cannot end as "
                + "Cancelled: that state means no commit was produced.");
        }

        State = terminalState;
        Outcome = null;
        UpdatedAtUtc = nowUtc;
        ClearLease();
    }

    /// <summary>Marks the worktree as released. Never legal while it holds the only copy of the work.</summary>
    public void MarkWorkspaceReleased(DateTimeOffset nowUtc)
    {
        if (WorktreeMustBePreserved)
        {
            throw new DomainException(
                $"Execution {Id} has no result commit, so its worktree holds the only copy of the work (D6).");
        }

        WorkspaceState = WorkspaceState.Released;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkWorkspaceOrphaned(DateTimeOffset nowUtc)
    {
        WorkspaceState = WorkspaceState.Orphaned;
        UpdatedAtUtc = nowUtc;
    }
}
