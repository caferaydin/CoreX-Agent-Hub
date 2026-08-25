namespace CoreX.Hub.Api;

/// <summary>
/// What the wire carries.
///
/// These are not the domain model with a different name. They are shaped for the screens
/// Phase 9B has to draw, which is why a task summary carries its latest execution's state
/// and a step carries whether evidence exists: the alternative is a client that fans out
/// four requests to render one row.
///
/// Every enum crosses as a string and every timestamp as UTC ISO-8601, so a TypeScript
/// client never depends on an ordinal and never has to guess a zone.
/// </summary>
public sealed record OrganizationDto(Guid Id, string Slug, string Name);

public sealed record ProjectDto(Guid Id, Guid OrganizationId, string Slug, string Name, string TaskKeyPrefix);

public sealed record RepositoryDto
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; init; }

    public required string DefaultBranch { get; init; }

    public IReadOnlyList<string> ProtectedBranches { get; init; } = [];

    /// <summary>Which profile reviews here. The Phase 8 switch is visible as data.</summary>
    public string? ReviewerProfile { get; init; }

    public IReadOnlyList<string> VerificationProfiles { get; init; } = [];

    public string? DefaultVerificationProfile { get; init; }

    /// <summary>
    /// Whether the configured local path is present on this machine.
    ///
    /// A boolean rather than the path: the operator already knows where their repository is,
    /// and a browser response is not the place to publish a filesystem layout.
    /// </summary>
    public required bool LocalPathExists { get; init; }
}

public sealed record TaskSummaryDto
{
    public required Guid Id { get; init; }

    public required string TaskKey { get; init; }

    public required string Objective { get; init; }

    public required string State { get; init; }

    public required Guid RepositoryId { get; init; }

    public required string RepositorySlug { get; init; }

    public required int AttemptCount { get; init; }

    public Guid? LatestExecutionId { get; init; }

    public string? LatestExecutionState { get; init; }

    public string? LatestExecutionOutcome { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record TaskPageDto(IReadOnlyList<TaskSummaryDto> Items, int Total, int Offset, int Limit);

public sealed record CriterionDto(Guid Id, int Order, string Kind, string Text, string? VerificationRef, string? HumanCheckHint);

public sealed record TaskDetailDto
{
    public required TaskSummaryDto Summary { get; init; }

    public IReadOnlyList<string> AllowedScope { get; init; } = [];

    public IReadOnlyList<string> ForbiddenScope { get; init; } = [];

    public IReadOnlyList<CriterionDto> Criteria { get; init; } = [];

    public required bool ReadyGateSatisfied { get; init; }

    public IReadOnlyList<string> ReadyGateProblems { get; init; } = [];

    public IReadOnlyList<ExecutionSummaryDto> Attempts { get; init; } = [];

    public AcceptanceStateDto? Acceptance { get; init; }
}

public sealed record ExecutionSummaryDto
{
    public required Guid Id { get; init; }

    public required int AttemptNo { get; init; }

    public required string State { get; init; }

    public string? Outcome { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }
}

public sealed record ExecutionDetailDto
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    public required string State { get; init; }

    public string? Outcome { get; init; }

    public string? BaseCommitSha { get; init; }

    public string? ResultCommitSha { get; init; }

    public string? BranchName { get; init; }

    public string? ContextPackHash { get; init; }

    /// <summary>The frozen workspace disposition, as persisted. Never recomputed from disk.</summary>
    public string? WorkspaceState { get; init; }

    /// <summary>Set when the agent created commits the Hub did not ask for.</summary>
    public required bool UnexpectedAgentCommitDetected { get; init; }

    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public IReadOnlyList<ExecutionStepDto> Steps { get; init; } = [];
}

public sealed record ExecutionStepDto
{
    public required int Order { get; init; }

    public required string Kind { get; init; }

    public required string State { get; init; }

    public string? StateReason { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>How many evidence artifacts this step owns, so the UI knows what to offer.</summary>
    public required int EvidenceCount { get; init; }
}

public sealed record VerificationCommandDto
{
    public required int Order { get; init; }

    public required string Name { get; init; }

    public required bool Required { get; init; }

    public required string Status { get; init; }

    public int? ExitCode { get; init; }

    public long? DurationMilliseconds { get; init; }

    public int? TestsPassed { get; init; }

    public int? TestsFailed { get; init; }

    public int? TestsSkipped { get; init; }
}

public sealed record FindingDto
{
    public required Guid Id { get; init; }

    public required string Severity { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    public string? Rationale { get; init; }

    public string? File { get; init; }

    public int? Line { get; init; }

    public string? SuggestedFix { get; init; }

    public required string Confidence { get; init; }

    /// <summary>The Hub's own scope computation. Never the provider's claim.</summary>
    public required bool OutOfScope { get; init; }
}

public sealed record ReviewDto
{
    public IReadOnlyList<FindingDto> Findings { get; init; } = [];

    /// <summary>How many in-scope findings of each severity, by the severity's own name.</summary>
    public IReadOnlyDictionary<string, int> InScopeCountsBySeverity { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The single severity authority, echoed so a client never hardcodes it.</summary>
    public required int MajorThreshold { get; init; }

    /// <summary>The gate's answer. Not recomputed anywhere on the way out.</summary>
    public required bool Blocks { get; init; }

    /// <summary>The gate's own description of what it counted.</summary>
    public required string Summary { get; init; }
}

public sealed record EvidenceDto
{
    public required Guid Id { get; init; }

    public required string Kind { get; init; }

    public Guid? ExecutionStepId { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required string MediaType { get; init; }

    public required int RedactionCount { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record AcceptanceStateDto
{
    public required string TaskState { get; init; }

    public required Guid ExecutionId { get; init; }

    public required int AttemptNo { get; init; }

    public required bool IsLatestExecution { get; init; }

    public required string ExecutionState { get; init; }

    public string? ExecutionOutcome { get; init; }

    /// <summary>
    /// Whether the override route is the applicable one for this attempt.
    ///
    /// Computed server-side from the frozen rule, and sent so the UI can render honestly -
    /// never so the UI can decide. The server refuses an override on anything else whatever
    /// this says.
    /// </summary>
    public required bool OverrideApplicable { get; init; }

    public string? ExistingDecision { get; init; }

    public IReadOnlyList<MachineCriterionDto> MachineCriteria { get; init; } = [];

    public IReadOnlyList<HumanCriterionDto> HumanCriteria { get; init; } = [];

    public IReadOnlyList<string> MachineTruthProblems { get; init; } = [];
}

public sealed record MachineCriterionDto
{
    public required Guid CriterionId { get; init; }

    public required int Order { get; init; }

    public required string Text { get; init; }

    public required string VerificationRef { get; init; }

    public required string Verdict { get; init; }

    public required string VerificationStatus { get; init; }

    public required bool Required { get; init; }

    public int? ExitCode { get; init; }

    public int? TestsPassed { get; init; }

    public int? TestsFailed { get; init; }

    public int? TestsSkipped { get; init; }
}

public sealed record HumanCriterionDto(Guid CriterionId, int Order, string Text, string? HumanCheckHint);

public sealed record AcceptanceResultDto
{
    public required string TaskKey { get; init; }

    public required Guid ExecutionId { get; init; }

    public required int AttemptNo { get; init; }

    public required string Decision { get; init; }

    public required bool OverrideUsed { get; init; }

    public required string TaskState { get; init; }

    public required string ExecutionState { get; init; }

    public string? ExecutionOutcome { get; init; }

    public string? Reason { get; init; }

    public required string WorkspaceCleanup { get; init; }
}

// --- Requests -----------------------------------------------------------------------------

public sealed record CreateTaskBody
{
    public required string OrganizationSlug { get; init; }

    public required string ProjectSlug { get; init; }

    public string? RepositorySlug { get; init; }

    public required string Objective { get; init; }

    public string? TaskKey { get; init; }

    public IReadOnlyList<string> AllowedScope { get; init; } = [];

    public IReadOnlyList<string> ForbiddenScope { get; init; } = [];

    public IReadOnlyList<CriterionBody> Criteria { get; init; } = [];

    public required string Actor { get; init; }
}

public sealed record CriterionBody(string Kind, string Text, string? VerificationRef, string? HumanCheckHint);

public sealed record ActorBody
{
    public required string Actor { get; init; }
}

public sealed record RunBody
{
    public required string Actor { get; init; }

    /// <summary>Which implementer profile runs. The reviewer is never chosen here.</summary>
    public required string AgentProfile { get; init; }
}

public sealed record RunStartedDto
{
    public required Guid ExecutionId { get; init; }

    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    /// <summary>Where to watch it. The request does not wait for the run.</summary>
    public required string EventsUrl { get; init; }
}

public sealed record HumanVerdictBody(Guid CriterionId, string Verdict, string? Note);

public sealed record AcceptBody
{
    public required string Actor { get; init; }

    public IReadOnlyList<HumanVerdictBody> HumanVerdicts { get; init; } = [];
}

/// <summary>
/// Accepting despite a blocking review.
///
/// A separate body on a separate route, so overriding can never be something that happens
/// because a field defaulted to true.
/// </summary>
public sealed record OverrideBody
{
    public required string Actor { get; init; }

    public required string Reason { get; init; }

    public IReadOnlyList<HumanVerdictBody> HumanVerdicts { get; init; } = [];
}

public sealed record DecisionBody
{
    public required string Actor { get; init; }

    public string? Reason { get; init; }
}

// --- Diagnostics --------------------------------------------------------------------------

public sealed record HealthDto
{
    public required string Status { get; init; }

    public required bool DatabaseReachable { get; init; }

    public required bool ConfigurationLoaded { get; init; }

    public required int PendingRecoveryCount { get; init; }

    public required DateTimeOffset NowUtc { get; init; }
}

public sealed record AgentProfileDto
{
    public required string Name { get; init; }

    public required string Role { get; init; }

    public required string Provider { get; init; }

    public required string RuntimeKey { get; init; }

    public required string PermissionProfile { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    /// <summary>Whether the installation declares a runtime under this profile's key.</summary>
    public required bool RuntimeDeclared { get; init; }
}
