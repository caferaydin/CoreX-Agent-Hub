using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Deciding in a different process from the one that ran the work.
///
/// This is the ordinary case, not an edge one: a run finishes, the machine is done with it,
/// and some time later a person looks. Everything the decision needs therefore has to come
/// out of SQLite - so every test here closes the database between the run and the decision,
/// and again between the decision and reading it back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcceptanceRestartTests
{
    private const string HumanCriterion = "The wording reads well.";

    [Fact]
    public async Task A_run_can_be_accepted_by_a_process_that_never_saw_it()
    {
        await using var hub = await OrchestrationHarness.CreateAsync(humanCriteria: [HumanCriterion]);

        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        // Everything the running process knew is gone from here on.
        var store = await hub.ReopenStoreAsync();
        var service = AcceptanceFixture.ServiceFor(store);

        var preview = await service.PreviewAsync("HUB-001", 1, null, CancellationToken.None);

        Assert.Equal(TaskState.AwaitingAcceptance, preview.TaskState);
        Assert.Equal(ExecutionOutcome.Passed, preview.ExecutionOutcome);
        Assert.True(preview.IsLatestExecution);
        Assert.Empty(preview.MachineTruthProblems);

        // Machine truth was rebuilt from the database, not remembered.
        var machine = Assert.Single(preview.MachineCriteria);
        Assert.Equal("test", machine.VerificationRef);
        Assert.Equal(MachineCriterionVerdict.Met, machine.Verdict);
        Assert.Equal(VerificationCommandStatus.Passed, machine.VerificationStatus);
        Assert.True(machine.Required);

        var criterion = Assert.Single(preview.HumanCriteria);
        Assert.Equal(HumanCriterion, criterion.Text);

        // The person still has to answer, machine truth or not.
        var incomplete = await Assert.ThrowsAsync<AcceptanceException>(() => service.AcceptAsync(
            new AcceptanceRequest { TaskKey = "HUB-001", AttemptNo = 1, Actor = "cafer" },
            hub.Clock.GetUtcNow(),
            CancellationToken.None));

        Assert.Contains("needs a verdict", incomplete.Message, StringComparison.Ordinal);

        var result = await service.AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                HumanVerdicts =
                [
                    new HumanCriterionVerdictRecord(criterion.CriterionId, HumanCriterionVerdict.Met, "read it"),
                ],
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(ExecutionOutcome.Passed, result.ExecutionOutcome);

        // And once more, in a third process: the decision and the state are on disk.
        var afterwards = await hub.ReopenStoreAsync();

        var task = await afterwards.FindTaskAsync(hub.TaskId, CancellationToken.None);
        Assert.Equal(TaskState.Accepted, task!.State);

        var execution = await afterwards.FindExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Passed, execution!.Outcome);

        var decision = await afterwards.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal(AcceptanceDecisionKind.Accept, decision!.Decision);
        Assert.Equal("cafer", decision.Actor);
        Assert.False(decision.OverrideUsed);

        var verdict = Assert.Single(decision.HumanCriterionVerdicts);
        Assert.Equal(HumanCriterionVerdict.Met, verdict.Verdict);
        Assert.Equal("read it", verdict.Note);

        // And it stays the only one.
        Assert.Equal(1, await afterwards.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task An_override_survives_the_process_that_made_it()
    {
        await using var hub = await OrchestrationHarness.CreateAsync(humanCriteria: [HumanCriterion]);

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub, new AcceptanceFixture.ReviewBlockedOptions());

        var verdicts = await AcceptanceFixture.HumanVerdictsAsync(hub);

        await AcceptanceFixture.Service(hub).AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                UseOverride = true,
                Reason = "known limitation, shipping anyway",
                HumanVerdicts = verdicts,
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        var store = await hub.ReopenStoreAsync();

        var decision = await store.FindAcceptanceDecisionAsync(execution.Id, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.True(decision!.OverrideUsed);
        Assert.Equal("known limitation, shipping anyway", decision.Reason);

        var reloaded = await store.FindExecutionAsync(execution.Id, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.ReviewBlocked, reloaded!.Outcome);

        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);
        Assert.Equal(TaskState.Accepted, task!.State);

        // The findings the override answered for are still there, unchanged.
        Assert.NotEmpty(await store.ListFindingsAsync(execution.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_optional_machine_failure_is_still_readable_after_a_restart()
    {
        await using var hub = await OrchestrationHarness.CreateAsync(
            humanCriteria: [HumanCriterion],
            machineCriteria: [("lint", "The linter is clean.")],
            verificationProfile: (org, repo, now) =>
                AcceptanceTests.Profile(org, repo, now, ("lint", false, "trx-broken", 1)));

        await AcceptanceFixture.RunPassingAsync(hub);

        var service = AcceptanceFixture.ServiceFor(await hub.ReopenStoreAsync());
        var preview = await service.PreviewAsync("HUB-001", 1, null, CancellationToken.None);

        var lint = Assert.Single(preview.MachineCriteria, m => m.VerificationRef == "lint");

        Assert.Equal(MachineCriterionVerdict.NotMet, lint.Verdict);
        Assert.False(lint.Required);
        Assert.Equal(VerificationCommandStatus.Failed, lint.VerificationStatus);

        // Not a problem, just a result: the execution's own outcome already weighed it.
        Assert.Empty(preview.MachineTruthProblems);
        Assert.Equal(ExecutionOutcome.Passed, preview.ExecutionOutcome);
    }
}
