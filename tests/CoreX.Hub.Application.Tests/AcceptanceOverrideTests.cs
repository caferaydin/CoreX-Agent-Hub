using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Options = CoreX.Hub.Application.Tests.AcceptanceFixture.ReviewBlockedOptions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The review override: accepting work a reviewer blocked.
///
/// It exists so that a person can say "I have seen the blocker and I am taking
/// responsibility". That is only meaningful if the blocker really was the single thing in
/// the way, so every other gate has to have passed - and the findings themselves survive
/// the decision untouched, because an override is an admission, not a correction.
///
/// Phase 5 has no reviewer, so these executions are persisted directly in the shape a
/// review step would leave. Nothing here adds one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcceptanceOverrideTests
{
    private const string HumanCriterion = "The wording reads well.";

    private static Task<OrchestrationHarness> HubAsync() =>
        OrchestrationHarness.CreateAsync(humanCriteria: [HumanCriterion]);

    private static async Task<AcceptanceRequest> OverrideAsync(
        OrchestrationHarness hub,
        string? reason = "The blocker is a known limitation and the release cannot wait.") =>
        new()
        {
            TaskKey = "HUB-001",
            AttemptNo = 1,
            Actor = "cafer",
            UseOverride = true,
            Reason = reason,
            HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
        };

    [Fact]
    public async Task A_blocked_review_can_be_overridden_when_nothing_else_failed()
    {
        await using var hub = await HubAsync();
        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options());

        var findingsBefore = await hub.Store.ListFindingsAsync(execution.Id, CancellationToken.None);
        Assert.NotEmpty(findingsBefore);

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            await OverrideAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.True(result.OverrideUsed);
        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Contains("known limitation", result.Reason!, StringComparison.Ordinal);

        // The execution keeps the outcome it earned. Overriding is a decision about it, not
        // a revision of it.
        var afterwards = await hub.ExecutionAsync(execution.Id);
        Assert.Equal(ExecutionOutcome.ReviewBlocked, afterwards.Outcome);
        Assert.NotNull(afterwards.ResultCommitSha);

        // I-A8: the reviewer's findings are exactly as they were.
        var findingsAfter = await hub.Store.ListFindingsAsync(execution.Id, CancellationToken.None);

        Assert.Equal(findingsBefore.Count, findingsAfter.Count);
        Assert.Equal(
            findingsBefore.Select(f => (f.Id, f.Severity, f.Title, f.OutOfScope)),
            findingsAfter.Select(f => (f.Id, f.Severity, f.Title, f.OutOfScope)));

        // The reason reaches the audit trail; ADR-0014 does not allow a silent override.
        var audit = await hub.AuditEventsAsync(execution.Id);
        var overridden = Assert.Single(audit, a => a.EventType == "AcceptanceOverridden");

        Assert.Contains("review override", overridden.Summary, StringComparison.Ordinal);
        Assert.Contains("known limitation", overridden.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionOutcome.Passed)]
    [InlineData(ExecutionOutcome.VerificationFailed)]
    [InlineData(ExecutionOutcome.ReviewUnusable)]
    [InlineData(ExecutionOutcome.ScopeViolation)]
    [InlineData(ExecutionOutcome.SecretGuardBlocked)]
    [InlineData(ExecutionOutcome.RuntimePolicyViolation)]
    public async Task No_other_outcome_can_be_overridden(ExecutionOutcome outcome)
    {
        // The closed set. ScopeViolation and SecretGuardBlocked are here for a second
        // reason too: an execution that failed either gate cannot be ReviewBlocked, so this
        // is also what proves those gates passed for the one outcome that can be.
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options { Outcome = outcome });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Theory]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.TimedOut)]
    [InlineData(ExecutionState.Cancelled)]
    [InlineData(ExecutionState.Interrupted)]
    public async Task An_execution_that_never_completed_cannot_be_overridden(ExecutionState terminal)
    {
        await using var hub = await HubAsync();

        var now = hub.Clock.GetUtcNow();
        var task = await hub.TaskAsync();

        var execution = new Domain.Executions.Execution(
            Guid.CreateVersion7(), hub.OrganizationId, hub.TaskId, 1, new string('a', 40),
            "agent/HUB-001/1", Path.Combine(hub.WorkspaceRoot, "HUB-001-1"), null, now);

        execution.Terminate(terminal, now);

        task.MarkRunning(now);
        task.ReturnToReady("it did not finish", now);

        hub.Store.AddExecution(execution);
        await hub.Store.SaveChangesAsync(CancellationToken.None);

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task An_override_may_never_be_silent()
    {
        await using var hub = await HubAsync();
        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options());

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub, reason: "   "));
        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub, reason: null));
    }

    [Fact]
    public async Task An_agent_step_that_did_not_succeed_stops_the_override()
    {
        await using var hub = await HubAsync();
        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options { AgentSucceeded = false });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Theory]
    [InlineData(VerificationCommandStatus.Failed)]
    [InlineData(VerificationCommandStatus.TimedOut)]
    [InlineData(VerificationCommandStatus.Cancelled)]
    [InlineData(VerificationCommandStatus.StartFailed)]
    [InlineData(VerificationCommandStatus.ParseFailed)]
    [InlineData(VerificationCommandStatus.Skipped)]
    public async Task Required_verification_that_did_not_pass_stops_the_override(VerificationCommandStatus status)
    {
        // Read from what was recorded, including whether the step was required at the time.
        // ParseFailed is the one worth naming: it exits zero, and an override decided from
        // exit codes would let it through.
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { RequiredVerification = status });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task Verification_truth_that_was_never_recorded_stops_the_override()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { WithVerificationCommands = false });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task An_execution_with_no_result_commit_has_nothing_to_accept()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { Outcome = ExecutionOutcome.ScopeViolation, WithResultCommit = false });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
        Assert.Null((await hub.ExecutionAsync(execution.Id)).ResultCommitSha);
    }

    [Fact]
    public async Task An_unexpected_agent_commit_is_not_a_review_finding()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { UnexpectedAgentCommit = true });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task There_must_be_a_blocking_finding_to_answer_for()
    {
        await using var hub = await HubAsync();

        var noFinding = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { WithBlockingFinding = false });

        await AssertRefusedAsync(hub, noFinding, await OverrideAsync(hub));
    }

    [Fact]
    public async Task A_finding_outside_the_diff_is_not_something_to_override()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { FindingIsOutOfScope = true });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Theory]
    [InlineData(FindingSeverity.Minor)]
    [InlineData(FindingSeverity.Info)]
    public async Task A_review_that_only_found_minor_or_info_is_not_something_to_override(
        FindingSeverity severity)
    {
        // The execution says ReviewBlocked, but what was actually recorded does not block.
        // Trusting the outcome alone would let a person answer for a blocker nobody found -
        // so the same gate the review is judged by is asked again here.
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { FindingSeverity = severity });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Theory]
    [InlineData(FindingSeverity.Critical)]
    [InlineData(FindingSeverity.Major)]
    public async Task A_review_that_found_something_blocking_can_be_overridden(FindingSeverity severity)
    {
        // Critical blocks alone; Major blocks at the frozen threshold of one.
        await using var hub = await HubAsync();

        await AcceptanceFixture.PersistCompletedAsync(hub, new Options { FindingSeverity = severity });

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            await OverrideAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.True(result.OverrideUsed);
        Assert.Equal(TaskState.Accepted, result.TaskState);
    }

    [Fact]
    public async Task An_out_of_scope_critical_is_not_something_to_override()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { FindingSeverity = FindingSeverity.Critical, FindingIsOutOfScope = true });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task An_override_never_releases_the_worktree()
    {
        // A blocked review is a lifecycle Phase 7 owns. This fix releases only the case G.6
        // states plainly, and an override is not it.
        await using var hub = await HubAsync();

        await AcceptanceFixture.PersistCompletedAsync(hub, new Options());

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            await OverrideAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(WorkspaceCleanupOutcome.NotAttempted, result.WorkspaceCleanup.Outcome);
        Assert.Contains("review override", result.WorkspaceCleanup.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_that_is_not_ready_is_not_in_the_override_situation()
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new Options { Outcome = ExecutionOutcome.Passed, TaskState = TaskState.AwaitingAcceptance });

        await AssertRefusedAsync(hub, execution, await OverrideAsync(hub));
    }

    [Fact]
    public async Task An_older_blocked_attempt_cannot_be_overridden_once_a_newer_one_exists()
    {
        // I-A11. Attempt 1 satisfies every prerequisite on its own; what disqualifies it is
        // that the work has moved on and accepting it would accept something superseded.
        await using var hub = await HubAsync();

        var older = await AcceptanceFixture.PersistCompletedAsync(hub, new Options { AttemptNo = 1 });
        var newer = await AcceptanceFixture.PersistCompletedAsync(hub, new Options { AttemptNo = 2 });

        Assert.Equal(2, newer.AttemptNo);

        await AssertRefusedAsync(hub, older, await OverrideAsync(hub));

        // And the newer one, which is the current attempt, is overridable.
        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            (await OverrideAsync(hub)) with { AttemptNo = 2 },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.True(result.OverrideUsed);
        Assert.Equal(TaskState.Accepted, result.TaskState);
    }

    /// <summary>Nothing was written and nothing moved: the refusal left no trace but the error.</summary>
    private static async Task AssertRefusedAsync(
        OrchestrationHarness hub,
        Domain.Executions.Execution execution,
        AcceptanceRequest request)
    {
        var stateBefore = (await hub.TaskAsync()).State;
        var outcomeBefore = (await hub.ExecutionAsync(execution.Id)).Outcome;

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(request, hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
        Assert.Equal(stateBefore, (await hub.TaskAsync()).State);
        Assert.Equal(outcomeBefore, (await hub.ExecutionAsync(execution.Id)).Outcome);
    }
}
