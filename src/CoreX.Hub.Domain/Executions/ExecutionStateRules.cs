namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Pure classification of execution states. Used by the database's
/// "at most one active execution per task" filtered index and by the reconciler.
/// </summary>
public static class ExecutionStateRules
{
    /// <summary>States from which the pipeline can still move forward.</summary>
    public static readonly ExecutionState[] NonTerminalStates =
    [
        ExecutionState.Created,
        ExecutionState.PreparingWorkspace,
        ExecutionState.BuildingContext,
        ExecutionState.RunningAgent,
        ExecutionState.CapturingChanges,
        ExecutionState.Verifying,
        ExecutionState.Reviewing,
    ];

    public static bool IsTerminal(ExecutionState state) => !NonTerminalStates.Contains(state);

    /// <summary>
    /// True only for <see cref="ExecutionState.Completed"/>. Failed / TimedOut / Cancelled /
    /// Interrupted are terminal but NOT completed and therefore carry no outcome.
    /// </summary>
    public static bool IsCompleted(ExecutionState state) => state == ExecutionState.Completed;

    /// <summary>
    /// Errata E1 / ADR-0016: outcomes for which the Hub never creates a result commit and
    /// the worktree must be preserved.
    /// </summary>
    public static bool ProducesResultCommit(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Passed => true,
        ExecutionOutcome.VerificationFailed => true,
        ExecutionOutcome.ReviewBlocked => true,
        ExecutionOutcome.ReviewUnusable => true,
        ExecutionOutcome.ScopeViolation => false,
        ExecutionOutcome.SecretGuardBlocked => false,
        ExecutionOutcome.RuntimePolicyViolation => false,
        _ => false,
    };
}
