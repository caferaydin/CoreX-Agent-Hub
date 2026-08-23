using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Options = CoreX.Hub.Application.Tests.AcceptanceFixture.ReviewBlockedOptions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The two decisions that are not acceptance: send it back, or end it.
///
/// Neither judges the work against the criteria, so neither demands the checklist - a person
/// who has seen enough to ask for changes should not have to answer every question first.
/// Both leave the execution exactly as it was: what happened already happened, and a
/// decision about it is not a revision of it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskDecisionTests
{
    private static Task<OrchestrationHarness> HubAsync() =>
        OrchestrationHarness.CreateAsync(humanCriteria: ["The wording reads well."]);

    private static TaskDecisionRequest Request(string? reason = null, int attempt = 1) => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = attempt,
        Actor = "cafer",
        Reason = reason,
    };

    [Fact]
    public async Task Sending_the_work_back_returns_the_task_to_the_queue()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var result = await AcceptanceFixture.Service(hub).RequestChangesAsync(
            Request("the empty state still looks wrong"), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.RequestChanges, result.Decision);
        Assert.False(result.OverrideUsed);
        Assert.Equal(TaskState.Ready, result.TaskState);

        var task = await hub.TaskAsync();
        Assert.Equal(TaskState.Ready, task.State);
        Assert.Equal("the empty state still looks wrong", task.BlockReason);

        // Nothing about the execution changed, and nothing started another one.
        var execution = await hub.ExecutionAsync(executionId);
        Assert.Equal(ExecutionState.Completed, execution.State);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.NotNull(execution.ResultCommitSha);

        Assert.Single(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        var audit = await hub.AuditEventsAsync(executionId);
        Assert.Single(audit, a => a.EventType == "AcceptanceChangesRequested");
    }

    [Fact]
    public async Task Sending_the_work_back_needs_no_checklist()
    {
        await using var hub = await HubAsync();
        await AcceptanceFixture.RunPassingAsync(hub);

        // No verdicts supplied at all, and the task still has an unanswered human criterion.
        var result = await AcceptanceFixture.Service(hub).RequestChangesAsync(
            Request(), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Ready, result.TaskState);
        Assert.Empty(result.HumanVerdicts);
    }

    [Fact]
    public async Task Rejection_ends_the_task()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var result = await AcceptanceFixture.Service(hub).RejectAsync(
            Request("we are not doing this after all"), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.Reject, result.Decision);
        Assert.Equal(TaskState.Rejected, result.TaskState);
        Assert.True(TaskStateRules.IsTerminal((await hub.TaskAsync()).State));

        var execution = await hub.ExecutionAsync(executionId);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);

        var audit = await hub.AuditEventsAsync(executionId);
        Assert.Single(audit, a => a.EventType == "AcceptanceRejected");
    }

    [Fact]
    public async Task Neither_decision_can_be_taken_twice()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        await AcceptanceFixture.Service(hub).RequestChangesAsync(
            Request("once"), hub.Clock.GetUtcNow(), CancellationToken.None);

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).RejectAsync(
                Request("twice"), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));

        var recorded = await hub.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);
        Assert.Equal(AcceptanceDecisionKind.RequestChanges, recorded!.Decision);
        Assert.Equal("once", recorded.Reason);
    }

    [Theory]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.Running)]
    public async Task Neither_decision_applies_to_a_task_that_is_not_awaiting_one(TaskState state)
    {
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options
        {
            Outcome = state == TaskState.Ready ? ExecutionOutcome.ReviewBlocked : ExecutionOutcome.Passed,
            TaskState = state,
            WithBlockingFinding = state == TaskState.Ready,
        });

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).RejectAsync(
                Request("no"), hub.Clock.GetUtcNow(), CancellationToken.None));

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).RequestChangesAsync(
                Request("no"), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
        Assert.Equal(state, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task A_decision_names_an_attempt_that_exists()
    {
        await using var hub = await HubAsync();
        await AcceptanceFixture.RunPassingAsync(hub);

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).RejectAsync(
                Request("no", attempt: 7), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("no attempt 7", refused.Message, StringComparison.Ordinal);
    }
}
