using System.Runtime.Versioning;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// The decisions, over HTTP.
///
/// What these are really testing is that the HTTP layer is not an authority. Every case
/// below is a rule the application already enforces, asked again through a route - and the
/// point is that the route adds nothing, subtracts nothing, and cannot be talked out of it
/// by a well-formed request.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MutationEndpointTests
{
    [Fact]
    public async Task A_task_is_created_through_the_same_authority_the_console_uses()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (status, created) = await hub.PostAsync("/api/v1/tasks", new
        {
            organizationSlug = "test-org",
            projectSlug = "test-project",
            repositorySlug = "test-repo",
            objective = "write the thing",
            taskKey = "API-100",
            allowedScope = new[] { "docs/**" },
            criteria = new[] { new { kind = "Human", text = "it reads well", verificationRef = (string?)null, humanCheckHint = (string?)null } },
            actor = "operator",
        });

        Assert.Equal(201, status);
        Assert.Equal("API-100", created.GetProperty("taskKey").GetString());
        Assert.Equal("Draft", created.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_task_key_cannot_be_used_twice()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-101");

        var (status, body) = await hub.PostAsync("/api/v1/tasks", new
        {
            organizationSlug = "test-org",
            projectSlug = "test-project",
            repositorySlug = "test-repo",
            objective = "the same key again",
            taskKey = "API-101",
            actor = "operator",
        });

        // Refused by the catalogue, not by anything this endpoint knows. A browser that
        // resubmits a create form gets a refusal rather than a second task.
        Assert.Equal(409, status);
        Assert.Equal("task_refused", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ready_is_the_gate_answering_and_repeating_it_is_harmless()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-110", TaskState.Draft);

        var (first, one) = await hub.PostAsync("/api/v1/tasks/API-110/ready", new { actor = "operator" });

        Assert.Equal(200, first);
        Assert.True(one.GetProperty("transitioned").GetBoolean());
        Assert.Equal("Ready", one.GetProperty("task").GetProperty("state").GetString());

        // Idempotent by state rather than by a request identifier: the second call is a
        // non-transition, which is exactly what a double-clicked button should produce.
        var (second, two) = await hub.PostAsync("/api/v1/tasks/API-110/ready", new { actor = "operator" });

        Assert.Equal(200, second);
        Assert.False(two.GetProperty("transitioned").GetBoolean());
        Assert.Equal("Ready", two.GetProperty("task").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_passing_execution_is_accepted_once_and_only_once()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-120");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var verdicts = await HumanVerdictsAsync(hub, "API-120");

        var (status, accepted) = await hub.PostAsync(
            "/api/v1/tasks/API-120/attempts/1/accept",
            new { actor = "operator", humanVerdicts = verdicts });

        Assert.Equal(200, status);
        Assert.Equal("Accept", accepted.GetProperty("decision").GetString());
        Assert.False(accepted.GetProperty("overrideUsed").GetBoolean());
        Assert.Equal("Accepted", accepted.GetProperty("taskState").GetString());

        // Unchanged by the decision, and reported so that is visible rather than assumed.
        Assert.Equal("Completed", accepted.GetProperty("executionState").GetString());
        Assert.Equal("Passed", accepted.GetProperty("executionOutcome").GetString());

        // Two clicks, one decision. The state machine refuses the second, not the UI.
        var (again, _) = await hub.PostAsync(
            "/api/v1/tasks/API-120/attempts/1/accept",
            new { actor = "operator", humanVerdicts = verdicts });

        Assert.Equal(409, again);
    }

    [Fact]
    public async Task Overriding_a_passing_execution_is_refused()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-130");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-130/attempts/1/accept-override",
            new
            {
                actor = "operator",
                reason = "there is nothing here to override",
                humanVerdicts = await HumanVerdictsAsync(hub, "API-130"),
            });

        // The override route exists for one situation. Anything else is refused by the
        // service, whatever a client believes about which button it pressed.
        Assert.Equal(409, status);
        Assert.Equal("acceptance_refused", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_override_with_no_reason_is_refused(string reason)
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-140");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-140/attempts/1/accept-override",
            new
            {
                actor = "operator",
                reason,
                humanVerdicts = await HumanVerdictsAsync(hub, "API-140"),
            });

        Assert.Equal(400, status);
        Assert.Contains("reason", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_blocked_execution_is_accepted_only_by_override_and_stays_blocked()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-150");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var verdicts = await HumanVerdictsAsync(hub, "API-150");

        // The ordinary route will not take it.
        var (plain, _) = await hub.PostAsync(
            "/api/v1/tasks/API-150/attempts/1/accept",
            new { actor = "operator", humanVerdicts = verdicts });

        Assert.Equal(409, plain);

        var (status, accepted) = await hub.PostAsync(
            "/api/v1/tasks/API-150/attempts/1/accept-override",
            new
            {
                actor = "operator",
                reason = "accepted deliberately; the branch will not be merged",
                humanVerdicts = verdicts,
            });

        Assert.Equal(200, status);
        Assert.True(accepted.GetProperty("overrideUsed").GetBoolean());
        Assert.Equal("Accepted", accepted.GetProperty("taskState").GetString());

        // The override records a decision. It does not rewrite what the execution was.
        Assert.Equal("ReviewBlocked", accepted.GetProperty("executionOutcome").GetString());
        Assert.Contains("not be merged", accepted.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_acceptance_view_says_whether_the_override_route_applies()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var blocked = await hub.SeedTaskAsync("API-160");
        await hub.SeedExecutionAsync(blocked, ExecutionOutcome.ReviewBlocked);

        var passing = await hub.SeedTaskAsync("API-161");
        await hub.SeedExecutionAsync(passing, ExecutionOutcome.Passed);

        var (_, forBlocked) = await hub.GetAsync("/api/v1/tasks/API-160/attempts/1/acceptance");
        var (_, forPassing) = await hub.GetAsync("/api/v1/tasks/API-161/attempts/1/acceptance");

        // Computed on the server so the UI can render honestly - and, as the tests above
        // show, enforced on the server so it does not matter if the UI does not.
        Assert.True(forBlocked.GetProperty("overrideApplicable").GetBoolean());
        Assert.False(forPassing.GetProperty("overrideApplicable").GetBoolean());
    }

    [Fact]
    public async Task Request_changes_and_reject_stay_distinct_decisions()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var one = await hub.SeedTaskAsync("API-170");
        await hub.SeedExecutionAsync(one, ExecutionOutcome.Passed);

        var (changes, sentBack) = await hub.PostAsync(
            "/api/v1/tasks/API-170/attempts/1/request-changes",
            new { actor = "operator", reason = "not quite" });

        Assert.Equal(200, changes);
        Assert.Equal("RequestChanges", sentBack.GetProperty("decision").GetString());
        Assert.Equal("Ready", sentBack.GetProperty("taskState").GetString());

        var two = await hub.SeedTaskAsync("API-171");
        await hub.SeedExecutionAsync(two, ExecutionOutcome.Passed);

        var (reject, rejected) = await hub.PostAsync(
            "/api/v1/tasks/API-171/attempts/1/reject",
            new { actor = "operator", reason = "not wanted after all" });

        Assert.Equal(200, reject);
        Assert.Equal("Reject", rejected.GetProperty("decision").GetString());
        Assert.NotEqual(
            sentBack.GetProperty("taskState").GetString(),
            rejected.GetProperty("taskState").GetString());
    }

    [Fact]
    public async Task A_verdict_the_vocabulary_does_not_contain_is_refused()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-180");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var criterion = (await HumanVerdictsAsync(hub, "API-180")).Single();

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-180/attempts/1/accept",
            new
            {
                actor = "operator",
                humanVerdicts = new[] { new { criterionId = criterion.CriterionId, verdict = "Probably", note = (string?)null } },
            });

        Assert.Equal(400, status);
        Assert.Contains("Met", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_may_not_launch_refuses_to_start_a_run()
    {
        await using var hub = await HubApiFixture.CreateAsync(allowExecutionLaunch: false);

        await hub.SeedTaskAsync("API-190", TaskState.Ready);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-190/runs",
            new { actor = "operator", agentProfile = "scripted-implementer" });

        Assert.Equal(409, status);
        Assert.Equal("execution_refused", body.GetProperty("code").GetString());
    }

    /// <summary>The Human criteria this attempt is waiting on, read from the acceptance view.</summary>
    private static async Task<IReadOnlyList<HumanVerdictBody>> HumanVerdictsAsync(
        HubApiFixture hub,
        string taskKey)
    {
        var (_, acceptance) = await hub.GetAsync($"/api/v1/tasks/{taskKey}/attempts/1/acceptance");

        return
        [
            .. acceptance.GetProperty("humanCriteria").EnumerateArray()
                .Select(c => new HumanVerdictBody(c.GetProperty("criterionId").GetGuid(), "Met", "looks right")),
        ];
    }
}
