using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The ordinary acceptance route, and what it refuses.
///
/// The automatic gate has already decided whether the work is good; what a person adds is
/// judgement about the things a machine cannot check. So this route asks for their verdicts
/// and nothing else - it does not re-litigate the machine's answer, and it does not demand
/// that every machine criterion be met before a person may say yes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcceptanceTests
{
    private const string HumanCriterion = "The wording reads well.";

    private static Task<OrchestrationHarness> HubAsync(
        IReadOnlyList<(string Ref, string Text)>? extraMachineCriteria = null,
        Func<Guid, Guid, DateTimeOffset, Domain.Catalog.VerificationProfile>? profile = null) =>
        OrchestrationHarness.CreateAsync(
            humanCriteria: [HumanCriterion],
            machineCriteria: extraMachineCriteria,
            verificationProfile: profile);

    [Fact]
    public async Task A_passing_execution_is_accepted_once_a_person_has_answered()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.False(result.OverrideUsed);
        Assert.Equal(TaskState.Accepted, result.TaskState);

        // The execution is untouched: acceptance records a decision, it does not re-judge.
        Assert.Equal(ExecutionOutcome.Passed, result.ExecutionOutcome);

        var execution = await hub.ExecutionAsync(executionId);
        Assert.Equal(ExecutionState.Completed, execution.State);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);

        Assert.Equal(TaskState.Accepted, (await hub.TaskAsync()).State);
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));

        // Machine truth is reported back rather than asked for.
        var machine = Assert.Single(result.MachineCriteria);
        Assert.Equal("test", machine.VerificationRef);
        Assert.Equal(MachineCriterionVerdict.Met, machine.Verdict);
        Assert.Equal(VerificationCommandStatus.Passed, machine.VerificationStatus);
    }

    [Fact]
    public async Task An_optional_machine_failure_does_not_stand_in_the_way()
    {
        // The distinction the frozen model insists on: the execution's own outcome is the
        // automatic gate, and it already accounted for the optional step. A criterion
        // pointing at that step is honestly NotMet, and the person is shown it - but
        // "not every machine criterion is met" is not a rule anyone wrote down.
        await using var hub = await HubAsync(
            [("lint", "The linter is clean.")],
            (org, repo, now) => Profile(org, repo, now, ("lint", false, "trx-broken", 1)));

        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var preview = await AcceptanceFixture.Service(hub)
            .PreviewAsync("HUB-001", 1, null, CancellationToken.None);

        var test = Assert.Single(preview.MachineCriteria, m => m.VerificationRef == "test");
        var lint = Assert.Single(preview.MachineCriteria, m => m.VerificationRef == "lint");

        Assert.Equal(MachineCriterionVerdict.Met, test.Verdict);
        Assert.True(test.Required);

        Assert.Equal(MachineCriterionVerdict.NotMet, lint.Verdict);
        Assert.False(lint.Required);
        Assert.Equal(VerificationCommandStatus.Failed, lint.VerificationStatus);

        Assert.Empty(preview.MachineTruthProblems);

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(ExecutionOutcome.Passed, (await hub.ExecutionAsync(executionId)).Outcome);
    }

    [Fact]
    public async Task Machine_truth_that_cannot_be_established_stops_the_decision()
    {
        // Not the same as a criterion that is not met. The Hub cannot say what the machine
        // did, so there is nothing to accept against, and turning that into "not met" would
        // make an unanswerable question look answered.
        // The profile declares typecheck, so the task is legitimately Ready. The execution
        // then records no command for it - verification that never produced a result is
        // exactly how machine truth goes missing in practice.
        await using var hub = await HubAsync(
            [("typecheck", "The types check.")],
            (org, repo, now) => Profile(org, repo, now, ("typecheck", true, "trx-fixed", 0)));

        var execution = await AcceptanceFixture.PersistCompletedAsync(
            hub,
            new AcceptanceFixture.ReviewBlockedOptions
            {
                Outcome = ExecutionOutcome.Passed,
                TaskState = TaskState.AwaitingAcceptance,
                WithBlockingFinding = false,
            });

        var executionId = execution.Id;

        var preview = await AcceptanceFixture.Service(hub)
            .PreviewAsync("HUB-001", 1, null, CancellationToken.None);

        Assert.Single(preview.MachineTruthProblems);
        Assert.Contains("typecheck", preview.MachineTruthProblems[0], StringComparison.Ordinal);

        var verdicts = await AcceptanceFixture.HumanVerdictsAsync(hub);

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                new AcceptanceRequest
                {
                    TaskKey = "HUB-001",
                    AttemptNo = 1,
                    Actor = "cafer",
                    HumanVerdicts = verdicts,
                },
                hub.Clock.GetUtcNow(),
                CancellationToken.None));

        Assert.Contains("Machine truth", refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
        Assert.Equal(TaskState.AwaitingAcceptance, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task An_override_flag_on_a_passing_execution_is_refused()
    {
        // There is no gate to override, so the flag is a misunderstanding rather than a
        // no-op, and saying so is more useful than quietly ignoring it.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var verdicts = await AcceptanceFixture.HumanVerdictsAsync(hub);

        await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                new AcceptanceRequest
                {
                    TaskKey = "HUB-001",
                    AttemptNo = 1,
                    Actor = "cafer",
                    UseOverride = true,
                    Reason = "I want it anyway",
                    HumanVerdicts = verdicts,
                },
                hub.Clock.GetUtcNow(),
                CancellationToken.None));

        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
        Assert.Equal(TaskState.AwaitingAcceptance, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task A_stale_attempt_cannot_be_accepted_once_a_newer_one_exists()
    {
        await using var hub = await HubAsync();

        await AcceptanceFixture.PersistCompletedAsync(hub, new AcceptanceFixture.ReviewBlockedOptions
        {
            AttemptNo = 1,
            Outcome = ExecutionOutcome.Passed,
            TaskState = TaskState.AwaitingAcceptance,
            WithBlockingFinding = false,
        });

        var older = Assert.Single(
            await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        // A second attempt happens; the first is history now.
        var task = await hub.TaskAsync();
        task.ReturnToReady("another attempt", hub.Clock.GetUtcNow());
        await hub.Store.SaveChangesAsync(CancellationToken.None);

        await AcceptanceFixture.PersistCompletedAsync(hub, new AcceptanceFixture.ReviewBlockedOptions
        {
            AttemptNo = 2,
            Outcome = ExecutionOutcome.Passed,
            TaskState = TaskState.AwaitingAcceptance,
            WithBlockingFinding = false,
        });

        var verdicts = await AcceptanceFixture.HumanVerdictsAsync(hub);

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                new AcceptanceRequest
                {
                    TaskKey = "HUB-001",
                    AttemptNo = 1,
                    Actor = "cafer",
                    HumanVerdicts = verdicts,
                },
                hub.Clock.GetUtcNow(),
                CancellationToken.None));

        Assert.Contains("not the latest", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(older.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_decision_is_written_once_and_never_revised()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var request = new AcceptanceRequest
        {
            TaskKey = "HUB-001",
            AttemptNo = 1,
            Actor = "cafer",
            HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
        };

        await AcceptanceFixture.Service(hub).AcceptAsync(request, hub.Clock.GetUtcNow(), CancellationToken.None);

        var again = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(request, hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("never revised", again.Message, StringComparison.Ordinal);
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task The_audit_trail_records_who_decided_and_what_they_said()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        await AcceptanceFixture.Service(hub).AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        var audit = await hub.AuditEventsAsync(executionId);
        var accepted = Assert.Single(audit, a => a.EventType == "AcceptanceAccepted");

        Assert.Equal("cafer", accepted.Actor);
        Assert.Contains("HUB-001#1", accepted.Summary, StringComparison.Ordinal);
        Assert.Contains("1/1 human criteria met", accepted.Summary, StringComparison.Ordinal);
        Assert.Equal(executionId, accepted.ExecutionId);
    }

    /// <summary>A profile with the required "test" step plus one more the caller describes.</summary>
    internal static Domain.Catalog.VerificationProfile Profile(
        Guid organizationId,
        Guid repositoryId,
        DateTimeOffset now,
        (string Name, bool Required, string Report, int ExitCode) extra) =>
        new(
            Guid.CreateVersion7(),
            organizationId,
            repositoryId,
            "fixture-verification",
            true,
            ".",
            [
                new Domain.Catalog.VerificationStep(
                    "test", "fixture", ["report", "results/test.trx", "trx-fixed", "0"],
                    120, required: true, Domain.Catalog.VerificationResultParser.Trx, "results/test.trx"),
                new Domain.Catalog.VerificationStep(
                    extra.Name,
                    "fixture",
                    ["report", $"results/{extra.Name}.trx", extra.Report,
                     extra.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    120,
                    extra.Required,
                    Domain.Catalog.VerificationResultParser.Trx,
                    $"results/{extra.Name}.trx"),
            ],
            now);
}
