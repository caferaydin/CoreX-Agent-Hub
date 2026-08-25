using System.Runtime.Versioning;
using System.Text.Json;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// What a decision looks like to someone who was not the one who made it.
///
/// The response to the request that records a decision carries the whole truth, and that is
/// the easy case. These tests are about the other one: a person reloads the page, or opens it
/// tomorrow, or was never the operator who clicked - and the question "who accepted this, when,
/// and on what grounds" has to be answerable from a fresh read of durable state.
///
/// Every field asserted here is a column. Nothing is derived, nothing is defaulted, and an
/// undecided attempt reads back as undecided rather than as an empty decision.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DecisionReadbackTests
{
    [Fact]
    public async Task An_undecided_attempt_says_so_rather_than_returning_an_empty_decision()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-300");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var (status, acceptance) = await hub.GetAsync("/api/v1/tasks/API-300/attempts/1/acceptance");

        Assert.Equal(200, status);
        Assert.Equal(JsonValueKind.Null, acceptance.GetProperty("existingDecision").ValueKind);
    }

    [Fact]
    public async Task A_normal_acceptance_reads_back_whole_from_a_fresh_request()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-301");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var criteria = await HumanCriteriaAsync(hub, "API-301");

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var (accepted, _) = await hub.PostAsync(
            "/api/v1/tasks/API-301/attempts/1/accept",
            new
            {
                actor = "hub:operator:ui",
                humanVerdicts = criteria.Select(c => new { criterionId = c, verdict = "Met", note = "read it" }),
            });

        Assert.Equal(200, accepted);

        // A separate request, answered from the row rather than from anything the accept call
        // is still holding.
        var (status, acceptance) = await hub.GetAsync("/api/v1/tasks/API-301/attempts/1/acceptance");

        Assert.Equal(200, status);

        var decision = acceptance.GetProperty("existingDecision");

        Assert.Equal("Accept", decision.GetProperty("decision").GetString());
        Assert.Equal("hub:operator:ui", decision.GetProperty("actor").GetString());
        Assert.False(decision.GetProperty("overrideUsed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("reason").ValueKind);

        var decidedAt = decision.GetProperty("decidedAtUtc").GetDateTimeOffset();

        Assert.InRange(decidedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));

        var verdict = Assert.Single(decision.GetProperty("humanVerdicts").EnumerateArray());

        Assert.Equal(criteria[0], verdict.GetProperty("criterionId").GetGuid());
        Assert.Equal("Met", verdict.GetProperty("verdict").GetString());
        Assert.Equal("read it", verdict.GetProperty("note").GetString());
    }

    [Fact]
    public async Task An_override_reads_back_with_its_reason_and_leaves_the_execution_blocked()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-302");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var criteria = await HumanCriteriaAsync(hub, "API-302");

        var (accepted, _) = await hub.PostAsync(
            "/api/v1/tasks/API-302/attempts/1/accept-override",
            new
            {
                actor = "hub:operator:ui",
                reason = "the finding is about generated code we do not own",
                humanVerdicts = criteria.Select(c => new { criterionId = c, verdict = "NotApplicable", note = (string?)null }),
            });

        Assert.Equal(200, accepted);

        var (status, acceptance) = await hub.GetAsync("/api/v1/tasks/API-302/attempts/1/acceptance");

        Assert.Equal(200, status);

        var decision = acceptance.GetProperty("existingDecision");

        Assert.Equal("Accept", decision.GetProperty("decision").GetString());
        Assert.True(decision.GetProperty("overrideUsed").GetBoolean());
        Assert.Equal(
            "the finding is about generated code we do not own",
            decision.GetProperty("reason").GetString());

        var verdict = Assert.Single(decision.GetProperty("humanVerdicts").EnumerateArray());

        Assert.Equal("NotApplicable", verdict.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, verdict.GetProperty("note").ValueKind);

        // The two axes stay apart on a fresh read as well: the task was accepted, and the
        // execution is still what the review found it to be.
        Assert.Equal("Accepted", acceptance.GetProperty("taskState").GetString());
        Assert.Equal("ReviewBlocked", acceptance.GetProperty("executionOutcome").GetString());

        var (_, execution) = await hub.GetAsync(
            $"/api/v1/executions/{acceptance.GetProperty("executionId").GetGuid()}");

        Assert.Equal("ReviewBlocked", execution.GetProperty("outcome").GetString());

        // And the finding that blocked it is still there to be read.
        var (_, review) = await hub.GetAsync(
            $"/api/v1/executions/{acceptance.GetProperty("executionId").GetGuid()}/review");

        Assert.NotEmpty(review.GetProperty("findings").EnumerateArray());
    }

    [Theory]
    [InlineData("request-changes", "RequestChanges")]
    [InlineData("reject", "Reject")]
    public async Task Sending_work_back_and_ending_it_read_back_as_the_decisions_they_are(
        string route,
        string expected)
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var key = "API-31" + (route == "reject" ? "1" : "0");
        var task = await hub.SeedTaskAsync(key);
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var (posted, _) = await hub.PostAsync(
            $"/api/v1/tasks/{key}/attempts/1/{route}",
            new { actor = "hub:operator:ui", reason = "not yet" });

        Assert.Equal(200, posted);

        var (_, acceptance) = await hub.GetAsync($"/api/v1/tasks/{key}/attempts/1/acceptance");
        var decision = acceptance.GetProperty("existingDecision");

        Assert.Equal(expected, decision.GetProperty("decision").GetString());
        Assert.Equal("hub:operator:ui", decision.GetProperty("actor").GetString());
        Assert.False(decision.GetProperty("overrideUsed").GetBoolean());
        Assert.Equal("not yet", decision.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_recorded_decision_never_carries_an_execution_outcome()
    {
        // Accepting a task and what its execution was are two facts. A decision that carried
        // an outcome would invite a client to render them as one, which is the exact confusion
        // the override exists not to create.
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-320");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var criteria = await HumanCriteriaAsync(hub, "API-320");

        await hub.PostAsync(
            "/api/v1/tasks/API-320/attempts/1/accept-override",
            new
            {
                actor = "operator",
                reason = "deliberate",
                humanVerdicts = criteria.Select(c => new { criterionId = c, verdict = "Met", note = (string?)null }),
            });

        var (_, acceptance) = await hub.GetAsync("/api/v1/tasks/API-320/attempts/1/acceptance");

        var names = acceptance.GetProperty("existingDecision").EnumerateObject()
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(
            ["actor", "decidedAtUtc", "decision", "humanVerdicts", "overrideUsed", "reason"],
            names.Order(StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyList<Guid>> HumanCriteriaAsync(HubApiFixture hub, string taskKey)
    {
        var (_, acceptance) = await hub.GetAsync($"/api/v1/tasks/{taskKey}/attempts/1/acceptance");

        return
        [
            .. acceptance.GetProperty("humanCriteria").EnumerateArray()
                .Select(c => c.GetProperty("criterionId").GetGuid()),
        ];
    }
}
