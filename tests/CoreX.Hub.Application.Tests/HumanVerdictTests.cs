using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// What the checklist demands, and what it deliberately does not.
///
/// Each Human criterion is asked once and answered once. The answer itself is not judged:
/// "not met" and "not applicable" are things a person is entitled to conclude, and refusing
/// to record them would turn the checklist into a formality that only ever says yes. What is
/// enforced is that the questions were the task's own, that none was skipped or answered
/// twice, and that nothing was answered on the machine's behalf.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HumanVerdictTests
{
    private const string First = "The wording reads well.";
    private const string Second = "The screenshot matches the ticket.";

    private static Task<OrchestrationHarness> HubAsync() =>
        OrchestrationHarness.CreateAsync(humanCriteria: [First, Second]);

    private static AcceptanceRequest Request(IReadOnlyList<HumanCriterionVerdictRecord> verdicts) => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = 1,
        Actor = "cafer",
        HumanVerdicts = verdicts,
    };

    private static async Task<IReadOnlyList<AcceptanceCriterion>> HumanCriteriaAsync(OrchestrationHarness hub) =>
        [.. (await hub.TaskAsync()).AcceptanceCriteria.Where(c => c.Kind == VerificationKind.Human)];

    [Fact]
    public async Task Every_human_criterion_has_to_be_answered()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var criteria = await HumanCriteriaAsync(hub);
        var onlyOne = new[] { new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.Met, null) };

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                Request(onlyOne), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("needs a verdict", refused.Message, StringComparison.Ordinal);
        Assert.Contains(Second, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task A_criterion_cannot_be_answered_twice()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var criteria = await HumanCriteriaAsync(hub);

        var duplicated = new[]
        {
            new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.Met, null),
            new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.NotMet, null),
            new HumanCriterionVerdictRecord(criteria[1].Id, HumanCriterionVerdict.Met, null),
        };

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                Request(duplicated), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("more than once", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task A_criterion_from_somewhere_else_is_refused()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var criteria = await HumanCriteriaAsync(hub);

        var stranger = new[]
        {
            new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.Met, null),
            new HumanCriterionVerdictRecord(criteria[1].Id, HumanCriterionVerdict.Met, null),
            new HumanCriterionVerdictRecord(Guid.CreateVersion7(), HumanCriterionVerdict.Met, null),
        };

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                Request(stranger), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("does not belong", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task A_machine_criterion_cannot_be_answered_by_hand()
    {
        // I-A6 at the service boundary. The machine's answer comes from evidence, and a
        // request that could carry one would be a request that could overrule it.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var task = await hub.TaskAsync();
        var machine = task.AcceptanceCriteria.Single(c => c.Kind == VerificationKind.Machine);
        var human = task.AcceptanceCriteria.Where(c => c.Kind == VerificationKind.Human).ToList();

        var supplied = new[]
        {
            new HumanCriterionVerdictRecord(human[0].Id, HumanCriterionVerdict.Met, null),
            new HumanCriterionVerdictRecord(human[1].Id, HumanCriterionVerdict.Met, null),
            new HumanCriterionVerdictRecord(machine.Id, HumanCriterionVerdict.Met, "trust me"),
        };

        var refused = await Assert.ThrowsAsync<AcceptanceException>(() =>
            AcceptanceFixture.Service(hub).AcceptAsync(
                Request(supplied), hub.Clock.GetUtcNow(), CancellationToken.None));

        Assert.Contains("a human verdict is not what decides it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task All_three_verdicts_and_their_notes_are_recorded_as_given()
    {
        // Including the ones that are not positive. A person who looked and concluded "not
        // met" has answered the question, and the record says so.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var criteria = await HumanCriteriaAsync(hub);

        var given = new[]
        {
            new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.NotMet, "the heading is still wrong"),
            new HumanCriterionVerdictRecord(criteria[1].Id, HumanCriterionVerdict.NotApplicable, null),
        };

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            Request(given), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);

        var recorded = await hub.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(2, recorded!.HumanCriterionVerdicts.Count);

        var first = recorded.HumanCriterionVerdicts.Single(v => v.CriterionId == criteria[0].Id);
        var second = recorded.HumanCriterionVerdicts.Single(v => v.CriterionId == criteria[1].Id);

        Assert.Equal(HumanCriterionVerdict.NotMet, first.Verdict);
        Assert.Equal("the heading is still wrong", first.Note);

        Assert.Equal(HumanCriterionVerdict.NotApplicable, second.Verdict);
        Assert.Null(second.Note);
    }

    [Fact]
    public async Task Met_round_trips_unchanged_too()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var criteria = await HumanCriteriaAsync(hub);

        await AcceptanceFixture.Service(hub).AcceptAsync(
            Request(
            [
                new HumanCriterionVerdictRecord(criteria[0].Id, HumanCriterionVerdict.Met, "checked on staging"),
                new HumanCriterionVerdictRecord(criteria[1].Id, HumanCriterionVerdict.Met, null),
            ]),
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        var recorded = await hub.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);

        Assert.All(recorded!.HumanCriterionVerdicts, v => Assert.Equal(HumanCriterionVerdict.Met, v.Verdict));
        Assert.Contains(recorded.HumanCriterionVerdicts, v => v.Note == "checked on staging");
    }
}
