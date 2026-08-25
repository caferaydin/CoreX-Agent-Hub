using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// What the read surface says, over real HTTP against a real installation.
///
/// These are contract tests. The Phase 9B client will be written against exactly these
/// shapes, so what is asserted is the shape and the vocabulary - string enums, UTC
/// timestamps, stable ids - as much as the values.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QueryEndpointTests
{
    [Fact]
    public async Task Health_answers_without_reaching_a_provider()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (status, body) = await hub.GetAsync("/api/v1/health");

        Assert.Equal(200, status);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("databaseReachable").GetBoolean());
        Assert.True(body.GetProperty("configurationLoaded").GetBoolean());

        // UTC, ISO-8601, parseable by a browser without a timezone guess.
        Assert.EndsWith("Z", body.GetProperty("nowUtc").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalogue_is_navigable_from_organization_to_repository()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (_, organizations) = await hub.GetAsync("/api/v1/organizations");
        var organization = organizations.EnumerateArray().Single();

        Assert.Equal("test-org", organization.GetProperty("slug").GetString());

        var (_, projects) = await hub.GetAsync(
            $"/api/v1/projects?organizationId={organization.GetProperty("id").GetString()}");

        var project = projects.EnumerateArray().Single();

        Assert.Equal("API", project.GetProperty("taskKeyPrefix").GetString());

        var (_, repositories) = await hub.GetAsync("/api/v1/repositories");
        var repository = repositories.EnumerateArray().Single();

        Assert.Equal("test-repo", repository.GetProperty("slug").GetString());
        Assert.Equal("main", repository.GetProperty("defaultBranch").GetString());

        // Whether the checkout is there, never where it is.
        Assert.True(repository.TryGetProperty("localPathExists", out _));
        Assert.False(repository.TryGetProperty("localPath", out _));
    }

    [Fact]
    public async Task A_repository_reports_which_profile_reviews_it()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (_, repositories) = await hub.GetAsync("/api/v1/repositories");
        var repository = repositories.EnumerateArray().Single();

        // Null here rather than absent: this fixture declares no reviewer, and the future UI
        // has to be able to tell "not reviewed" from "not loaded".
        Assert.True(repository.TryGetProperty("reviewerProfile", out var reviewer));
        Assert.Equal(JsonValueKind.Null, reviewer.ValueKind);
    }

    [Fact]
    public async Task A_task_list_is_ordered_paged_and_carries_its_latest_attempt()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var first = await hub.SeedTaskAsync("API-001");
        await hub.SeedTaskAsync("API-002");
        await hub.SeedExecutionAsync(first, ExecutionOutcome.Passed);

        var (status, page) = await hub.GetAsync("/api/v1/tasks?limit=1");

        Assert.Equal(200, status);
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());

        var items = page.GetProperty("items").EnumerateArray().ToList();
        var only = Assert.Single(items);

        // Newest first, deterministically.
        Assert.Equal("API-002", only.GetProperty("taskKey").GetString());

        var (_, second) = await hub.GetAsync("/api/v1/tasks?limit=1&offset=1");
        var older = second.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("API-001", older.GetProperty("taskKey").GetString());

        // One row renders without a second request: the latest attempt travels with it.
        Assert.Equal(1, older.GetProperty("attemptCount").GetInt32());
        Assert.Equal("Completed", older.GetProperty("latestExecutionState").GetString());
        Assert.Equal("Passed", older.GetProperty("latestExecutionOutcome").GetString());
    }

    [Fact]
    public async Task A_task_detail_carries_scope_criteria_and_the_ready_gate()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-010", TaskState.Draft);

        var (status, task) = await hub.GetAsync("/api/v1/tasks/API-010");

        Assert.Equal(200, status);
        Assert.Equal("Draft", task.GetProperty("summary").GetProperty("state").GetString());
        Assert.Equal("docs/**", task.GetProperty("allowedScope").EnumerateArray().Single().GetString());

        var criteria = task.GetProperty("criteria").EnumerateArray().ToList();

        Assert.Equal(2, criteria.Count);
        Assert.Contains(criteria, c => c.GetProperty("kind").GetString() == "Machine");
        Assert.Contains(criteria, c => c.GetProperty("kind").GetString() == "Human");

        // The gate's own answer, not a re-derivation.
        Assert.True(task.TryGetProperty("readyGateSatisfied", out _));
    }

    [Fact]
    public async Task An_execution_reports_its_commits_its_steps_and_its_workspace()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-020");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var (status, detail) = await hub.GetAsync($"/api/v1/executions/{execution.Id:D}");

        Assert.Equal(200, status);
        Assert.Equal("API-020", detail.GetProperty("taskKey").GetString());
        Assert.Equal("Completed", detail.GetProperty("state").GetString());
        Assert.Equal("Passed", detail.GetProperty("outcome").GetString());
        Assert.Equal(40, detail.GetProperty("baseCommitSha").GetString()!.Length);
        Assert.Equal(40, detail.GetProperty("resultCommitSha").GetString()!.Length);
        Assert.False(detail.GetProperty("unexpectedAgentCommitDetected").GetBoolean());
        Assert.True(detail.TryGetProperty("workspaceState", out _));

        // Steps are a list even when the fixture wrote none: a UI should never have to guard
        // against a missing collection.
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("steps").ValueKind);
    }

    [Fact]
    public async Task An_unknown_execution_is_a_stable_not_found_envelope()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (status, body) = await hub.GetAsync($"/api/v1/executions/{Guid.CreateVersion7():D}");

        Assert.Equal(404, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));

        // Nothing about the machine leaks into a routine refusal.
        var text = body.GetRawText();

        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_findings_come_back_with_the_gate_that_judged_them()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-030");

        // The fixture already writes the blocking finding a ReviewBlocked execution must
        // have, which is exactly what this endpoint should hand back.
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var (status, review) = await hub.GetAsync($"/api/v1/executions/{execution.Id:D}/review");

        Assert.Equal(200, status);

        var finding = review.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal("Major", finding.GetProperty("severity").GetString());
        Assert.Equal("High", finding.GetProperty("confidence").GetString());
        Assert.False(finding.GetProperty("outOfScope").GetBoolean());

        // The gate's verdict and the gate's threshold, carried rather than recomputed.
        Assert.True(review.GetProperty("blocks").GetBoolean());
        Assert.Equal(ReviewFindingGate.MajorThreshold, review.GetProperty("majorThreshold").GetInt32());
        Assert.Equal(1, review.GetProperty("inScopeCountsBySeverity").GetProperty("Major").GetInt32());
    }

    [Fact]
    public async Task Agent_profiles_are_reported_without_a_provider_being_probed()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var (status, profiles) = await hub.GetAsync("/api/v1/agent-profiles");

        Assert.Equal(200, status);

        var profile = profiles.EnumerateArray().Single();

        Assert.Equal("scripted-implementer", profile.GetProperty("name").GetString());
        Assert.Equal("Implementer", profile.GetProperty("role").GetString());
        Assert.Equal("WorkspaceWrite", profile.GetProperty("permissionProfile").GetString());

        // Declared, which is a configuration fact - not healthy, which would need a process.
        Assert.True(profile.GetProperty("runtimeDeclared").GetBoolean());

        var text = profiles.GetRawText();

        Assert.DoesNotContain("ExecutablePath", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
    }
}
