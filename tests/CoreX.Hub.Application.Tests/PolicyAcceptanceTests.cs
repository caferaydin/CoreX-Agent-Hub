using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// ADR-0028: a repository's autopilot accepting work, and everything it leaves for a person.
///
/// The policy route is the plain route with the judgement taken out, so most of what is
/// worth proving here is a refusal: each test names one way an execution can be fine for a
/// person to accept and still not be the policy's to accept.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PolicyAcceptanceTests
{
    private const string Autopilot = "hub:autopilot";

    private static PolicyAcceptanceRequest Request(int attemptNo = 1) => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = attemptNo,
        Actor = Autopilot,
    };

    private static async Task<OrchestrationHarness> ReviewedHubAsync(
        IReadOnlyList<string>? humanCriteria = null,
        IReadOnlyList<(string Ref, string Text)>? machineCriteria = null,
        Func<Guid, Guid, DateTimeOffset, Domain.Catalog.VerificationProfile>? profile = null)
    {
        var hub = await OrchestrationHarness.CreateAsync(
            humanCriteria: humanCriteria, machineCriteria: machineCriteria, verificationProfile: profile);

        hub.Reviewer = ScriptedReviewer.Clean();
        return hub;
    }

    [Fact]
    public async Task Reviewed_work_that_meets_every_machine_criterion_is_accepted_and_says_so()
    {
        await using var hub = await ReviewedHubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var result = await AcceptanceFixture.Service(hub).AcceptByPolicyAsync(
            Request(), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.Equal(AcceptanceDecisionSource.Policy, result.Source);
        Assert.False(result.OverrideUsed);
        Assert.Empty(result.HumanVerdicts);
        Assert.Equal(TaskState.Accepted, (await hub.TaskAsync()).State);

        // The execution is untouched, exactly as on the person's route.
        var execution = await hub.ExecutionAsync(executionId);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);

        // The row says who decided, without anyone having to read meaning into an actor name.
        var decision = await hub.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);
        Assert.NotNull(decision);
        Assert.Equal(AcceptanceDecisionSource.Policy, decision.Source);
        Assert.Equal(Autopilot, decision.Actor);
        Assert.StartsWith("autopilot: all 1 machine criteria met (test)", decision.Reason, StringComparison.Ordinal);

        var audit = Assert.Single(
            await hub.AuditEventsAsync(executionId), a => a.EventType.StartsWith("Acceptance", StringComparison.Ordinal));
        Assert.Equal("AcceptanceAcceptedByPolicy", audit.EventType);
        Assert.Equal(Autopilot, audit.Actor);
        Assert.EndsWith("by policy", audit.Summary, StringComparison.Ordinal);

        // A passing execution that has been accepted is released the same way either route.
        Assert.Equal(WorkspaceCleanupOutcome.NotAttempted, result.WorkspaceCleanup.Outcome);
    }

    [Fact]
    public async Task A_task_with_a_human_criterion_is_left_for_a_person()
    {
        // ADR-0015 #6 still holds: a human criterion is answered by a human, and a policy
        // has no verdict to give - so it does not decide at all.
        await using var hub = await ReviewedHubAsync(humanCriteria: ["The wording reads well."]);
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var error = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptByPolicyAsync(Request(), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("human criteria", error.Message, StringComparison.Ordinal);
        await AssertUndecidedAsync(hub, executionId);
    }

    [Fact]
    public async Task Unreviewed_work_is_left_for_a_person()
    {
        // A repository without a reviewer passes with its Review step skipped. That is a pass
        // a person may accept; it is not one a policy may, because nothing looked at the work.
        await using var hub = await OrchestrationHarness.CreateAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var error = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptByPolicyAsync(Request(), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("no independent review ran", error.Message, StringComparison.Ordinal);
        await AssertUndecidedAsync(hub, executionId);
    }

    [Fact]
    public async Task An_unmet_optional_machine_criterion_is_left_for_a_person()
    {
        // The plain route accepts this, and AcceptanceTests says why. The policy does not: a
        // person looking at an unmet criterion can judge it, a policy can only count it.
        await using var hub = await ReviewedHubAsync(
            machineCriteria: [("lint", "The linter is clean.")],
            profile: (org, repo, now) => AcceptanceTests.Profile(org, repo, now, ("lint", false, "trx-broken", 1)));

        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var error = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptByPolicyAsync(Request(), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("not met", error.Message, StringComparison.Ordinal);
        Assert.Contains("lint", error.Message, StringComparison.Ordinal);
        await AssertUndecidedAsync(hub, executionId);
    }

    [Theory]
    [InlineData(ExecutionOutcome.ReviewBlocked)]
    [InlineData(ExecutionOutcome.VerificationFailed)]
    [InlineData(ExecutionOutcome.ReviewUnusable)]
    public async Task Work_that_did_not_pass_is_never_accepted_by_policy(ExecutionOutcome outcome)
    {
        await using var hub = await ReviewedHubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new AcceptanceFixture.ReviewBlockedOptions
        {
            Outcome = outcome,
            TaskState = TaskState.Ready,
        });

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptByPolicyAsync(Request(), hub.Clock.GetUtcNow(), CancellationToken.None));

        await AssertUndecidedAsync(hub, execution.Id);
    }

    [Fact]
    public async Task A_decision_a_person_already_took_is_not_taken_again()
    {
        await using var hub = await ReviewedHubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var person = await AcceptanceFixture.Service(hub).AcceptAsync(
            new AcceptanceRequest { TaskKey = "HUB-001", AttemptNo = 1, Actor = "cafer" },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(AcceptanceDecisionSource.Person, person.Source);

        var error = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptByPolicyAsync(Request(), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("already has a recorded decision", error.Message, StringComparison.Ordinal);

        var preview = await AcceptanceFixture.Service(hub).PreviewAsync("HUB-001", 1, null, CancellationToken.None);
        Assert.Equal(AcceptanceDecisionSource.Person, preview.ExistingDecision?.Source);
        Assert.Equal("cafer", preview.ExistingDecision?.Actor);
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    private static async Task AssertUndecidedAsync(OrchestrationHarness hub, Guid executionId)
    {
        Assert.Null(await hub.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None));
        Assert.NotEqual(TaskState.Accepted, (await hub.TaskAsync()).State);
    }
}
