using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Acceptance;

/// <summary>Why a human decision was refused. Always said out loud, never absorbed.</summary>
public sealed class AcceptanceException : Exception
{
    public AcceptanceException(string message) : base(message)
    {
    }
}

/// <summary>
/// What machine evidence says about one Machine criterion.
///
/// Two values on purpose. The verdict is what the criterion is worth; the status is what
/// actually happened, and it is kept because "not met" alone hides the difference between a
/// test that failed and a report that could not be read. A person deciding acceptance is
/// owed the second one.
/// </summary>
public sealed record MachineCriterionResult
{
    public required Guid CriterionId { get; init; }

    public required int Order { get; init; }

    public required string Text { get; init; }

    public required string VerificationRef { get; init; }

    public required MachineCriterionVerdict Verdict { get; init; }

    public required VerificationCommandStatus VerificationStatus { get; init; }

    /// <summary>Whether the step was required when the execution ran, not as configured now.</summary>
    public required bool Required { get; init; }

    public int? ExitCode { get; init; }

    public int? TestsPassed { get; init; }

    public int? TestsFailed { get; init; }

    public int? TestsSkipped { get; init; }
}

/// <summary>
/// What a Machine criterion is worth. Deliberately only two values: there is no third
/// verdict meaning "we could not tell", because that is not a verdict - it is a reason to
/// stop, and it is reported separately.
/// </summary>
public enum MachineCriterionVerdict
{
    Met,
    NotMet,
}

/// <summary>One Human criterion, with whatever context the author left for the person.</summary>
public sealed record HumanCriterionPrompt(Guid CriterionId, int Order, string Text, string? HumanCheckHint);

/// <summary>
/// Everything a person needs in front of them before deciding, and nothing they can change.
///
/// Machine results are here to be read. Human criteria are here to be answered. If
/// <see cref="MachineTruthProblems"/> is not empty the Hub could not establish what the
/// machine did, and acceptance will refuse - that is shown before anyone is asked to type a
/// verdict rather than after.
/// </summary>
public sealed record AcceptancePreview
{
    public required string TaskKey { get; init; }

    public required TaskState TaskState { get; init; }

    public required Guid ExecutionId { get; init; }

    public required int AttemptNo { get; init; }

    public required bool IsLatestExecution { get; init; }

    public required ExecutionState ExecutionState { get; init; }

    public ExecutionOutcome? ExecutionOutcome { get; init; }

    public string? ResultCommitSha { get; init; }

    public IReadOnlyList<MachineCriterionResult> MachineCriteria { get; init; } = [];

    public IReadOnlyList<HumanCriterionPrompt> HumanCriteria { get; init; } = [];

    /// <summary>Machine truth that could not be established. Non-empty means accept will refuse.</summary>
    public IReadOnlyList<string> MachineTruthProblems { get; init; } = [];

    /// <summary>Set once a decision exists, because there is only ever one.</summary>
    public RecordedDecision? ExistingDecision { get; init; }
}

/// <summary>
/// A decision that was taken, read back from the row that holds it.
///
/// Every field here is persisted and none of it is derived. It exists because a decision that
/// can only be seen in the response to the request that made it is not a record - a person who
/// reloads the page, or who was not the one who clicked, would have no way to learn who
/// accepted what, when, or on what grounds.
///
/// There is exactly one of these per execution, so this is a projection of one row rather than
/// an audit history. Nothing here is a list of what might have happened.
/// </summary>
public sealed record RecordedDecision
{
    public required AcceptanceDecisionKind Decision { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset DecidedAtUtc { get; init; }

    /// <summary>True only for the ReviewBlocked acceptance override (ADR-0014).</summary>
    public required bool OverrideUsed { get; init; }

    /// <summary>
    /// Why, where one was given.
    ///
    /// Mandatory for an override and optional for everything else, which is a domain rule
    /// rather than a projection one - <see cref="AcceptanceDecision"/> refuses to exist with
    /// <see cref="OverrideUsed"/> set and no reason.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>What the person answered, exactly as it was recorded.</summary>
    public IReadOnlyList<HumanCriterionVerdictRecord> HumanVerdicts { get; init; } = [];

    /// <summary>A person, or a repository's autopilot (ADR-0028).</summary>
    public required AcceptanceDecisionSource Source { get; init; }
}

/// <summary>
/// A human accepting an execution.
///
/// There is deliberately no way to state a Machine verdict here. Machine results are read
/// from evidence and returned; a request that could carry one would be a request that could
/// overrule it (I-A6).
/// </summary>
public sealed record AcceptanceRequest
{
    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    public string? OrganizationSlug { get; init; }

    public required string Actor { get; init; }

    /// <summary>ADR-0014: accepting despite a blocking review. Never valid on a passing execution.</summary>
    public bool UseOverride { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<HumanCriterionVerdictRecord> HumanVerdicts { get; init; } = [];
}

/// <summary>
/// A repository's autopilot accepting an execution (ADR-0028).
///
/// Deliberately smaller than <see cref="AcceptanceRequest"/>: no override, no reason, no
/// human verdicts. A policy has nothing to add to the evidence - it can only notice that the
/// evidence already answered every criterion, and the service decides whether it did.
/// </summary>
public sealed record PolicyAcceptanceRequest
{
    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    public string? OrganizationSlug { get; init; }

    /// <summary>The policy's own name, for example <c>hub:autopilot</c>. Never a person's.</summary>
    public required string Actor { get; init; }
}

/// <summary>A human sending the work back, or ending it. Neither is an acceptance route.</summary>
public sealed record TaskDecisionRequest
{
    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    public string? OrganizationSlug { get; init; }

    public required string Actor { get; init; }

    public string? Reason { get; init; }
}

/// <summary>What a recorded decision changed, and what it deliberately did not.</summary>
public sealed record AcceptanceResult
{
    public required string TaskKey { get; init; }

    public required Guid ExecutionId { get; init; }

    public required int AttemptNo { get; init; }

    public required AcceptanceDecisionKind Decision { get; init; }

    public required bool OverrideUsed { get; init; }

    /// <summary>A person, or a repository's autopilot (ADR-0028).</summary>
    public required AcceptanceDecisionSource Source { get; init; }

    public required TaskState TaskState { get; init; }

    /// <summary>Unchanged by the decision. Reported so that is visible rather than assumed.</summary>
    public required ExecutionState ExecutionState { get; init; }

    public ExecutionOutcome? ExecutionOutcome { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<MachineCriterionResult> MachineCriteria { get; init; } = [];

    public IReadOnlyList<HumanCriterionVerdictRecord> HumanVerdicts { get; init; } = [];

    /// <summary>
    /// What happened to the worktree afterwards. Reported beside the decision rather than
    /// folded into it: the decision succeeded whatever this says.
    /// </summary>
    public WorkspaceCleanupResult WorkspaceCleanup { get; init; } =
        WorkspaceCleanupResult.NotAttempted("no cleanup was required");
}
