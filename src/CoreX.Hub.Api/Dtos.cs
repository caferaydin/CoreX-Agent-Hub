using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Api;

/// <summary>
/// Domain to wire, in one place.
///
/// Mapping only. Nothing here decides anything: where a boolean looks like a judgement -
/// whether a review blocks, whether an override applies - the value comes from the frozen
/// authority that owns it, and this just carries the answer outward.
/// </summary>
public static class Dtos
{
    public static bool IsTerminal(ExecutionState state) =>
        state is ExecutionState.Completed or ExecutionState.Failed or ExecutionState.Cancelled;

    public static OrganizationDto Organization(Organization o) => new(o.Id, o.Slug.Value, o.Name);

    public static ProjectDto Project(Project p) => new(p.Id, p.OrganizationId, p.Slug.Value, p.Name, p.TaskKeyPrefix);

    public static RepositoryDto Repository(
        Repository r,
        string? reviewerProfile,
        IReadOnlyList<VerificationProfile> profiles) => new()
        {
            Id = r.Id,
            ProjectId = r.ProjectId,
            Slug = r.Slug.Value,
            Name = r.Name,
            DefaultBranch = r.DefaultBranch,
            ProtectedBranches = r.ProtectedBranches,
            ReviewerProfile = reviewerProfile,
            VerificationProfiles = [.. profiles.Select(p => p.Name)],
            DefaultVerificationProfile = profiles.FirstOrDefault(p => p.IsDefault)?.Name,

            // Whether, not where. The path is a local filesystem fact and stays local.
            LocalPathExists = !string.IsNullOrWhiteSpace(r.LocalPath) && Directory.Exists(r.LocalPath),
        };

    public static TaskSummaryDto TaskSummary(
        HubTask task,
        Repository repository,
        IReadOnlyList<DomainExecution> attempts)
    {
        var latest = attempts.OrderByDescending(e => e.AttemptNo).FirstOrDefault();

        return new TaskSummaryDto
        {
            Id = task.Id,
            TaskKey = task.TaskKey.Value,
            Objective = task.Objective,
            State = task.State.ToString(),
            RepositoryId = task.RepositoryId,
            RepositorySlug = repository.Slug.Value,
            AttemptCount = attempts.Count,
            LatestExecutionId = latest?.Id,
            LatestExecutionState = latest?.State.ToString(),
            LatestExecutionOutcome = latest?.Outcome?.ToString(),
            CreatedAtUtc = task.CreatedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = task.UpdatedAtUtc.ToUniversalTime(),
        };
    }

    public static CriterionDto Criterion(AcceptanceCriterion c) => new(
        c.Id,
        c.Order,

        // The model's own word for it, and "Undeclared" where the model has none.
        //
        // Two earlier versions of this line invented a kind: one inferred it from which field
        // was set and reported every Human criterion as Machine, and the next defaulted to
        // Human and reported an undeclared criterion as a complete one - which hid the reason
        // the ready gate was refusing the task, on the very screen that shows the refusal.
        c.Kind?.ToString() ?? "Undeclared",
        c.Text,
        c.VerificationRef,
        c.HumanCheckHint);

    public static ExecutionSummaryDto ExecutionSummary(DomainExecution e) => new()
    {
        Id = e.Id,
        AttemptNo = e.AttemptNo,
        State = e.State.ToString(),
        Outcome = e.Outcome?.ToString(),
        CreatedAtUtc = e.CreatedAtUtc.ToUniversalTime(),
        UpdatedAtUtc = e.UpdatedAtUtc.ToUniversalTime(),
    };

    public static ExecutionDetailDto ExecutionDetail(
        DomainExecution e,
        HubTask task,
        IReadOnlyList<ExecutionStep> steps,
        IReadOnlyList<EvidenceArtifact> evidence) => new()
        {
            Id = e.Id,
            TaskId = e.TaskId,
            TaskKey = task.TaskKey.Value,
            AttemptNo = e.AttemptNo,
            State = e.State.ToString(),
            Outcome = e.Outcome?.ToString(),
            BaseCommitSha = e.BaseCommitSha,
            ResultCommitSha = e.ResultCommitSha,
            BranchName = e.BranchName,
            ContextPackHash = e.ContextPackHash,
            WorkspaceState = e.WorkspaceState.ToString(),
            UnexpectedAgentCommitDetected = e.UnexpectedAgentCommitDetected,
            UnexpectedCommitShas = Split(e.UnexpectedCommitShas),
            CreatedAtUtc = e.CreatedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = e.UpdatedAtUtc.ToUniversalTime(),
            Steps = Steps(steps, evidence),
        };

    public static IReadOnlyList<ExecutionStepDto> Steps(
        IReadOnlyList<ExecutionStep> steps,
        IReadOnlyList<EvidenceArtifact> evidence) =>
        [
            .. steps.OrderBy(s => s.Order).Select(s => new ExecutionStepDto
            {
                Order = s.Order,
                Kind = s.Kind.ToString(),
                State = s.State.ToString(),
                StateReason = s.StateReason,
                StartedAtUtc = s.StartedAtUtc?.ToUniversalTime(),
                EndedAtUtc = s.EndedAtUtc?.ToUniversalTime(),
                EvidenceCount = evidence.Count(a => a.ExecutionStepId == s.Id),
            }),
        ];

    public static VerificationCommandDto VerificationCommand(CommandResult c) => new()
    {
        Order = c.Order,
        Name = c.StepName,
        Required = c.Required,
        Status = c.Status.ToString(),
        ExitCode = c.ExitCode,
        DurationMilliseconds = c.DurationMilliseconds,
        TestsPassed = c.TestsPassed,
        TestsFailed = c.TestsFailed,
        TestsSkipped = c.TestsSkipped,
    };

    public static ReviewDto Review(IReadOnlyList<Finding> findings) => new()
    {
        Findings = [.. findings.Select(Finding)],

        // Counted, not judged. Which findings count is the gate's rule; how many there are of
        // each severity is arithmetic, and it is grouped by name so no severity constant is
        // written down twice.
        InScopeCountsBySeverity = findings
            .Where(ReviewFindingGate.ParticipatesInReview)
            .GroupBy(f => f.Severity.ToString())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),

        MajorThreshold = ReviewFindingGate.MajorThreshold,

        // Asked, never recomputed. The gate is the single severity authority (ADR-0022), and
        // an API that decided this for itself could disagree with the execution it describes.
        Blocks = ReviewFindingGate.HasBlockingFindings(findings),
        Summary = ReviewFindingGate.Describe(findings),
    };

    public static FindingDto Finding(Finding f) => new()
    {
        Id = f.Id,
        Severity = f.Severity.ToString(),
        Category = f.Category.ToString(),
        Title = f.Title,
        Rationale = f.Rationale,
        File = f.File,
        Line = f.Line,
        SuggestedFix = f.SuggestedFix,
        Confidence = f.Confidence.ToString(),
        OutOfScope = f.OutOfScope,
    };

    public static EvidenceDto Evidence(EvidenceArtifact a) => new()
    {
        Id = a.Id,
        Kind = a.Kind.ToString(),
        ExecutionStepId = a.ExecutionStepId,
        SizeBytes = a.SizeBytes,
        Sha256 = a.Sha256,
        MediaType = a.MediaType,
        RedactionCount = a.RedactionCount,
        CreatedAtUtc = a.CreatedAtUtc.ToUniversalTime(),
    };

    public static AcceptanceStateDto Acceptance(AcceptancePreview p) => new()
    {
        TaskState = p.TaskState.ToString(),
        ExecutionId = p.ExecutionId,
        AttemptNo = p.AttemptNo,
        IsLatestExecution = p.IsLatestExecution,
        ExecutionState = p.ExecutionState.ToString(),
        ExecutionOutcome = p.ExecutionOutcome?.ToString(),

        // The frozen rule, stated once: overriding is the route for a completed execution
        // that a review blocked, and for nothing else. Sent so the UI can render truthfully;
        // the server refuses regardless of what any client believes.
        OverrideApplicable = p.IsLatestExecution
                             && p.ExecutionState == ExecutionState.Completed
                             && p.ExecutionOutcome == ExecutionOutcome.ReviewBlocked,

        ExistingDecision = RecordedDecision(p.ExistingDecision),
        MachineCriteria = [.. p.MachineCriteria.Select(MachineCriterion)],
        HumanCriteria = [.. p.HumanCriteria.Select(h => new HumanCriterionDto(h.CriterionId, h.Order, h.Text, h.HumanCheckHint))],
        MachineTruthProblems = p.MachineTruthProblems,
    };

    /// <summary>The persisted decision, copied outward. Null stays null.</summary>
    public static RecordedDecisionDto? RecordedDecision(Application.Acceptance.RecordedDecision? d) =>
        d is null
            ? null
            : new RecordedDecisionDto
            {
                Decision = d.Decision.ToString(),
                Actor = d.Actor,
                DecidedAtUtc = d.DecidedAtUtc,
                OverrideUsed = d.OverrideUsed,
                Reason = d.Reason,
                HumanVerdicts =
                [
                    .. d.HumanVerdicts.Select(v =>
                        new RecordedVerdictDto(v.CriterionId, v.Verdict.ToString(), v.Note)),
                ],
                Source = d.Source.ToString(),
            };

    public static MachineCriterionDto MachineCriterion(MachineCriterionResult m) => new()
    {
        CriterionId = m.CriterionId,
        Order = m.Order,
        Text = m.Text,
        VerificationRef = m.VerificationRef,
        Verdict = m.Verdict.ToString(),
        VerificationStatus = m.VerificationStatus.ToString(),
        Required = m.Required,
        ExitCode = m.ExitCode,
        TestsPassed = m.TestsPassed,
        TestsFailed = m.TestsFailed,
        TestsSkipped = m.TestsSkipped,
    };

    public static AcceptanceResultDto AcceptanceResult(AcceptanceResult r) => new()
    {
        TaskKey = r.TaskKey,
        ExecutionId = r.ExecutionId,
        AttemptNo = r.AttemptNo,
        Decision = r.Decision.ToString(),
        OverrideUsed = r.OverrideUsed,
        TaskState = r.TaskState.ToString(),
        ExecutionState = r.ExecutionState.ToString(),
        ExecutionOutcome = r.ExecutionOutcome?.ToString(),
        Reason = r.Reason,
        WorkspaceCleanup = r.WorkspaceCleanup.Outcome.ToString(),
    };

    public static CancellationResultDto Cancellation(Application.Tasks.TaskCancellationResult r) => new()
    {
        State = r.State.ToString(),
        TaskKey = r.TaskKey,
        TaskState = r.TaskState.ToString(),
        ExecutionId = r.ExecutionId,
        ExecutionState = r.ExecutionState?.ToString(),
        ResultCommitSha = r.ResultCommitSha,
        WorktreePreserved = r.WorktreePreserved,
        Detail = r.Detail,
    };

    public static AgentProfileDto AgentProfile(AgentProfile p, bool runtimeDeclared) => new()
    {
        Name = p.Name,
        Role = p.Role.ToString(),
        Provider = p.Provider,
        RuntimeKey = p.RuntimeKey,
        PermissionProfile = p.PermissionProfile.ToString(),
        Model = p.Model,
        Effort = p.Effort,
        RuntimeDeclared = runtimeDeclared,
    };

    private static IReadOnlyList<string> Split(string? packed) =>
        string.IsNullOrWhiteSpace(packed)
            ? []
            : [.. packed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
