namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// The task transition table from architecture P.1, expressed as pure knowledge.
/// This is a lookup table, not a workflow engine: it answers "is this edge legal",
/// nothing more. Only <see cref="TaskState.Ready"/> is actually driven in Phase 1.
/// </summary>
public static class TaskStateRules
{
    private static readonly (TaskState From, TaskState To)[] Edges =
    [
        (TaskState.Draft, TaskState.Ready),
        (TaskState.Ready, TaskState.Running),
        (TaskState.Running, TaskState.AwaitingAcceptance),
        (TaskState.Running, TaskState.Ready),
        (TaskState.AwaitingAcceptance, TaskState.Accepted),
        (TaskState.AwaitingAcceptance, TaskState.Ready),
        (TaskState.AwaitingAcceptance, TaskState.Rejected),

        // ADR-0014 / errata E2: reachable ONLY through a review override acceptance.
        (TaskState.Ready, TaskState.Accepted),

        (TaskState.Draft, TaskState.Cancelled),
        (TaskState.Ready, TaskState.Cancelled),
        (TaskState.Running, TaskState.Cancelled),
        (TaskState.AwaitingAcceptance, TaskState.Cancelled),
    ];

    public static bool IsTerminal(TaskState state) =>
        state is TaskState.Accepted or TaskState.Rejected or TaskState.Cancelled;

    public static bool CanTransition(TaskState from, TaskState to) =>
        Edges.Contains((from, to));

    /// <summary>
    /// True for the single edge that a plain acceptance may never take.
    /// <c>Ready -&gt; Accepted</c> exists only as the review override path and additionally
    /// requires <see cref="Executions.AcceptanceRules.IsReviewOverrideEligible"/>.
    /// </summary>
    public static bool RequiresReviewOverride(TaskState from, TaskState to) =>
        from == TaskState.Ready && to == TaskState.Accepted;

    public static IReadOnlyList<(TaskState From, TaskState To)> AllEdges => Edges;
}
