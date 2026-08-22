namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// ADR-0014 / errata E2: the review override closed set.
///
/// The switch is written with an explicit <c>default: deny</c> so that adding a new
/// <see cref="ExecutionOutcome"/> member can never silently become overridable.
/// The service that consumes this rule arrives in Phase 5; the rule itself is domain
/// knowledge and is exhaustively tested today.
/// </summary>
public static class AcceptanceRules
{
    /// <summary>
    /// Whether a human may accept an execution with this outcome by explicitly overriding
    /// the review gate. Machine truth and runtime integrity failures are never overridable.
    /// </summary>
    public static bool IsReviewOverrideEligible(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.ReviewBlocked => true,
        _ => false,
    };

    /// <summary>
    /// Whether an execution with this outcome is eligible for plain (non-override) acceptance.
    /// </summary>
    public static bool IsPlainAcceptanceEligible(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Passed => true,
        _ => false,
    };
}
