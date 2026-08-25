using System.Runtime.Versioning;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The review as the pipeline sees it, through a real repository, a real agent process, real
/// verification and a real database.
///
/// The reviewer itself is scripted, because what is under test here is the orchestrator's
/// half of the contract: that a blocking review really does block, that its findings are
/// written down before the outcome that cites them, that the reviewer's transcript can be
/// told apart from the implementer's, and that none of it survives only in memory.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationReviewTests
{
    [Fact]
    public async Task A_clean_review_passes_and_the_review_step_says_it_ran()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Clean();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.Equal(TaskState.AwaitingAcceptance, (await hub.TaskAsync()).State);

        var review = await ReviewStepAsync(hub, report.ExecutionId);

        Assert.Equal(ExecutionStepState.Succeeded, review.State);
        Assert.NotNull(review.StartedAtUtc);
    }

    [Fact]
    public async Task A_blocking_review_ends_as_ReviewBlocked_and_returns_the_task_to_the_queue()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Blocking();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.ReviewBlocked, report.Outcome);

        // The commit is not disowned, and the task goes back to Ready - which is where a
        // human review override starts from.
        Assert.NotNull(report.ResultCommitSha);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
        Assert.True(AcceptanceRules.IsReviewOverrideEligible(report.Outcome!.Value));

        // A review that found a blocker did its job, so its step succeeded.
        Assert.Equal(ExecutionStepState.Succeeded, (await ReviewStepAsync(hub, report.ExecutionId)).State);
    }

    [Fact]
    public async Task Findings_are_written_down_before_the_outcome_that_cites_them()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Blocking();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        // Read through a fresh store: a ReviewBlocked whose findings existed only in memory
        // would be a refusal nobody could look up afterwards.
        var store = await hub.ReopenStoreAsync();

        var findings = await store.ListFindingsAsync(report.ExecutionId, CancellationToken.None);
        var review = (await store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None))
            .Single(s => s.Kind == ExecutionStepKind.Review);

        var finding = Assert.Single(findings);

        Assert.Equal(FindingSeverity.Major, finding.Severity);
        Assert.Equal(FindingCategory.Correctness, finding.Category);
        Assert.Equal(review.Id, finding.ExecutionStepId);
        Assert.False(finding.OutOfScope);
        Assert.True(ReviewFindingGate.HasBlockingFindings(findings));
    }

    [Fact]
    public async Task An_unusable_review_is_never_a_pass()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Unusable();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.ReviewUnusable, report.Outcome);

        // And it is not overridable either: only a review that worked and found something may
        // be argued with.
        Assert.False(AcceptanceRules.IsReviewOverrideEligible(report.Outcome!.Value));
        Assert.False(AcceptanceRules.IsPlainAcceptanceEligible(report.Outcome!.Value));

        Assert.Equal(ExecutionStepState.Failed, (await ReviewStepAsync(hub, report.ExecutionId)).State);
        Assert.Empty(await hub.Store.ListFindingsAsync(report.ExecutionId, CancellationToken.None));
    }

    [Fact]
    public async Task The_reviewer_transcript_can_be_told_apart_from_the_implementer_transcript()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Clean();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var store = await hub.ReopenStoreAsync();

        var steps = await store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        var review = steps.Single(s => s.Kind == ExecutionStepKind.Review);

        var evidence = await store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);

        var stdout = evidence.Where(e => e.Kind == EvidenceKind.AgentStdout).ToList();

        // Both producers stored one, and the step they belong to is what separates them.
        Assert.Equal(2, stdout.Count);
        Assert.Single(stdout, e => e.ExecutionStepId == review.Id);
        Assert.Single(stdout, e => e.ExecutionStepId is null);

        // The canonical findings document is the reviewer's, and it is step scoped too.
        var findings = Assert.Single(evidence, e => e.Kind == EvidenceKind.ReviewFindings);
        Assert.Equal(review.Id, findings.ExecutionStepId);
    }

    [Fact]
    public async Task A_reviewer_that_disagreed_about_scope_leaves_that_on_the_timeline()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Reviewer = ScriptedReviewer.Disagreeing();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);

        Assert.Contains(events, e => e.EventType == "ReviewScopeDisagreement");
        Assert.Contains(events, e => e.EventType == "ReviewClean");
        Assert.Contains(events, e => e.EventType == "ReviewAttempt");
    }

    [Fact]
    public async Task A_failed_verification_is_never_handed_to_a_reviewer()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.VerificationProfile = OrchestrationHarness.BuildVerificationProfile(
            hub.OrganizationId, hub.RepositoryId, "trx-fixed", 1, hub.Clock.GetUtcNow());

        var reviewer = ScriptedReviewer.Clean();
        hub.Reviewer = reviewer;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);
        Assert.Equal(0, reviewer.Calls);

        var review = await ReviewStepAsync(hub, report.ExecutionId);

        Assert.Equal(ExecutionStepState.Skipped, review.State);
        Assert.Equal("VerificationDidNotPass", review.StateReason);
    }

    [Fact]
    public async Task A_blocked_gate_still_reaches_no_reviewer()
    {
        await using var hub = await OrchestrationHarness.CreateAsync(allowedScope: ["docs/**"]);

        var reviewer = ScriptedReviewer.Clean();
        hub.Reviewer = reviewer;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.ScopeViolation, report.Outcome);
        Assert.Equal(0, reviewer.Calls);
    }

    private static async Task<ExecutionStep> ReviewStepAsync(OrchestrationHarness hub, Guid executionId) =>
        (await hub.Store.ListExecutionStepsAsync(executionId, CancellationToken.None))
        .Single(s => s.Kind == ExecutionStepKind.Review);
}

/// <summary>
/// A reviewer that returns a decided answer.
///
/// It is deliberately not a fake review engine: the engine's own rules are proved against
/// its ports in <see cref="ReviewEngineTests"/>. What this stands in for is the answer, so
/// that the orchestrator's handling of each kind of answer can be exercised without a
/// provider.
/// </summary>
public sealed class ScriptedReviewer : IExecutionReviewer
{
    private readonly ExecutionReviewResult _result;

    private ScriptedReviewer(ExecutionReviewResult result) => _result = result;

    public int Calls { get; private set; }

    public ExecutionReviewRequest? LastRequest { get; private set; }

    /// <summary>Runs when the review is asked for, before it answers.</summary>
    public Action? OnReview { get; set; }

    public Task<ExecutionReviewResult> ReviewAsync(ExecutionReviewRequest request, CancellationToken ct)
    {
        Calls++;
        LastRequest = request;

        OnReview?.Invoke();

        return Task.FromResult(_result);
    }

    public static ScriptedReviewer Clean() => new(new ExecutionReviewResult
    {
        Verdict = ReviewVerdict.Clean,
        StepFate = ReviewStepFate.Succeeded,
        Detail = "0 finding(s), 0 in scope",
        CanonicalDocument = "{\"summary\":\"nothing to report\",\"findings\":[]}",
        Attempts = [new ReviewAttemptEvidence(1, "session-1", "reviewer stdout", string.Empty, "{}")],
    });

    public static ScriptedReviewer Blocking() => new(new ExecutionReviewResult
    {
        Verdict = ReviewVerdict.Blocked,
        StepFate = ReviewStepFate.Succeeded,
        Detail = "1 finding(s), 1 in scope; 0 in-scope Critical, 1 in-scope Major",
        CanonicalDocument = "{\"summary\":\"one problem\",\"findings\":[]}",
        Attempts = [new ReviewAttemptEvidence(1, "session-1", "reviewer stdout", string.Empty, "{}")],
        Findings =
        [
            new CanonicalFinding
            {
                Severity = FindingSeverity.Major,
                Category = FindingCategory.Correctness,
                Title = "The loop never terminates",
                Rationale = "The counter is decremented in a branch that cannot be reached.",
                File = "src/One.cs",
                Line = 12,
                Confidence = FindingConfidence.High,
                OutOfScope = false,
            },
        ],
    });

    public static ScriptedReviewer Unusable() => new(new ExecutionReviewResult
    {
        Verdict = ReviewVerdict.Unusable,
        StepFate = ReviewStepFate.Failed,
        Detail = "the reviewer returned no usable structured output in 2 attempts",
        Attempts =
        [
            new ReviewAttemptEvidence(1, "session-1", "reviewer stdout", string.Empty, null),
            new ReviewAttemptEvidence(2, "session-2", "reviewer stdout", string.Empty, null),
        ],
    });

    /// <summary>A reviewer the caller stopped. The absence of a review, not a clean one.</summary>
    public static ScriptedReviewer Cancelled() => new(new ExecutionReviewResult
    {
        Verdict = ReviewVerdict.Unusable,
        StepFate = ReviewStepFate.Cancelled,
        Detail = "the review was cancelled before it produced a usable result",
        Attempts = [new ReviewAttemptEvidence(1, "session-1", "reviewer stdout", string.Empty, null)],
    });

    public static ScriptedReviewer Disagreeing() => new(new ExecutionReviewResult
    {
        Verdict = ReviewVerdict.Clean,
        StepFate = ReviewStepFate.Succeeded,
        Detail = "1 finding(s), 0 in scope",
        CanonicalDocument = "{\"summary\":\"one observation\",\"findings\":[]}",
        Attempts = [new ReviewAttemptEvidence(1, "session-1", "reviewer stdout", string.Empty, "{}")],
        ScopeDisagreements = ["src/Other.cs: reviewer said outOfScope=False, changed paths say True"],
        Findings =
        [
            new CanonicalFinding
            {
                Severity = FindingSeverity.Critical,
                Category = FindingCategory.Security,
                Title = "A problem in code this execution never touched",
                Rationale = "Recorded because it is worth knowing, and it decides nothing.",
                File = "src/Other.cs",
                Line = 3,
                Confidence = FindingConfidence.Low,
                OutOfScope = true,
            },
        ],
    });
}
