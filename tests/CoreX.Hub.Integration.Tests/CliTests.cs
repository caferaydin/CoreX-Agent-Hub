using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Phase 1 CLI surface. Every run uses an isolated temporary data directory and the
/// fixture configuration; no real repository or company data is involved.
/// </summary>
public sealed class CliTests
{
    [Fact]
    public async Task Org_list_shows_the_fixture_catalog_and_marks_the_blocked_organization()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync("org", "list");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hub-dev", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("isolation-gate-fixture", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Blocked", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Active", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("is blocked", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_list_can_be_scoped_to_one_organization()
    {
        using var hub = TempHub.Create();

        var all = await hub.RunAsync("project", "list");
        Assert.Equal(0, all.ExitCode);
        Assert.Contains("hub-sample", all.StdOut, StringComparison.Ordinal);
        Assert.Contains("gate-sample", all.StdOut, StringComparison.Ordinal);

        var scoped = await hub.RunAsync("project", "list", "--org", "hub-dev");
        Assert.Equal(0, scoped.ExitCode);
        Assert.Contains("hub-sample", scoped.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("gate-sample", scoped.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repo_list_shows_the_default_verification_profile()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync("repo", "list", "--org", "hub-dev", "--project", "hub-sample");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hub-sample-repo", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("dotnet-default", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("main", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_create_allocates_HUB_001_and_then_HUB_002()
    {
        using var hub = TempHub.Create();

        var first = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "first fixture task", "--allow", "src/**",
            "--machine-criterion", "test=all unit tests pass");

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("HUB-001", first.StdOut, StringComparison.Ordinal);
        Assert.Contains("Draft", first.StdOut, StringComparison.Ordinal);

        var second = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "second fixture task", "--allow", "src/**",
            "--human-criterion", "looks right");

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("HUB-002", second.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_show_renders_criteria_and_the_ready_gate()
    {
        using var hub = TempHub.Create();

        await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "show fixture task", "--allow", "src/**", "--forbid", "src/Migrations/**",
            "--machine-criterion", "build=builds without warnings",
            "--machine-criterion", "test=all unit tests pass",
            "--human-criterion", "matches the fixture scenario");

        var result = await hub.RunAsync("task", "show", "HUB-001");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("MACHINE", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("HUMAN", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("src/Migrations/**", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Ready gate: SATISFIED", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_ready_moves_a_valid_task_from_draft_to_ready()
    {
        using var hub = TempHub.Create();

        await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "ready fixture task", "--allow", "src/**",
            "--machine-criterion", "test=all unit tests pass",
            "--human-criterion", "matches the fixture scenario");

        var ready = await hub.RunAsync("task", "ready", "HUB-001");

        Assert.Equal(0, ready.ExitCode);
        Assert.Contains("Draft -> Ready", ready.StdOut, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        var task = await db.Tasks.SingleAsync();
        Assert.Equal(TaskState.Ready, task.State);
        Assert.Equal(2, task.Version);

        var audit = await db.AuditEvents.Where(a => a.EventType == "TaskMarkedReady").ToListAsync();
        Assert.Single(audit);
    }

    [Fact]
    public async Task Task_ready_refuses_a_criterion_without_a_declared_verification_kind()
    {
        using var hub = TempHub.Create();

        await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "undeclared fixture task", "--allow", "src/**",
            "--undeclared-criterion", "it should feel right");

        var ready = await hub.RunAsync("task", "ready", "HUB-001");

        Assert.Equal(3, ready.ExitCode);
        Assert.Contains("CriterionVerificationKindMissing", ready.StdOut, StringComparison.Ordinal);
        Assert.Contains("remains in Draft", ready.StdOut, StringComparison.Ordinal);

        await using var db = hub.NewContext();
        Assert.Equal(TaskState.Draft, (await db.Tasks.SingleAsync()).State);
    }

    [Fact]
    public async Task Task_ready_refuses_an_unresolved_machine_verification_reference()
    {
        using var hub = TempHub.Create();

        await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "unresolved fixture task", "--allow", "src/**",
            "--machine-criterion", "lint=lint is clean");

        var ready = await hub.RunAsync("task", "ready", "HUB-001");

        Assert.Equal(3, ready.ExitCode);
        Assert.Contains("MachineCriterionVerificationRefUnresolved", ready.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_ready_accepts_a_human_only_task_and_reports_the_no_machine_signal_warning()
    {
        using var hub = TempHub.Create();

        await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "human only fixture task", "--allow", "docs/**",
            "--human-criterion", "the wording reads well");

        var ready = await hub.RunAsync("task", "ready", "HUB-001");

        Assert.Equal(0, ready.ExitCode);
        Assert.Contains("NoMachineCriterion", ready.StdOut, StringComparison.Ordinal);
        Assert.Contains("Draft -> Ready", ready.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_in_a_blocked_organization_cannot_become_ready()
    {
        using var hub = TempHub.Create();

        var created = await hub.RunAsync(
            "task", "create", "--org", "isolation-gate-fixture", "--project", "gate-sample",
            "--objective", "gate fixture task", "--allow", "src/**",
            "--machine-criterion", "build=it builds");

        Assert.Equal(0, created.ExitCode);
        Assert.Contains("GATE-001", created.StdOut, StringComparison.Ordinal);

        var ready = await hub.RunAsync("task", "ready", "GATE-001");

        Assert.Equal(3, ready.ExitCode);
        Assert.Contains("OrganizationBlocked", ready.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_create_rejects_a_key_whose_prefix_does_not_match_the_project()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "GATE-001",
            "--objective", "wrong prefix", "--allow", "src/**", "--human-criterion", "looks right");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("prefix", result.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_commands_and_missing_options_are_usage_errors()
    {
        using var hub = TempHub.Create();

        var unknown = await hub.RunAsync("task", "run");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("Unknown command", unknown.StdErr, StringComparison.Ordinal);

        var missing = await hub.RunAsync("task", "create", "--org", "hub-dev");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("--project", missing.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phase_two_commands_are_not_implemented()
    {
        using var hub = TempHub.Create();

        foreach (var command in new[] { "run", "accept", "reject" })
        {
            var result = await hub.RunAsync("task", command);
            Assert.Equal(1, result.ExitCode);
        }

        foreach (var group in new[] { "workspace", "evidence", "doctor" })
        {
            var result = await hub.RunAsync(group, "list");
            Assert.Equal(1, result.ExitCode);
        }
    }

    [Fact]
    public async Task Help_is_available_and_documents_the_phase_boundary()
    {
        using var hub = TempHub.Create();

        var result = await hub.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hub task ready", result.StdOut, StringComparison.Ordinal);

        // Phase 10C-1 implemented the last of the deferred commands, so the help text no longer
        // names one. The boundary it has to document now is a different and less obvious shape:
        // hub doctor can report a correct machine and still say that executions are refused,
        // because measuring a hard environment is not the same capability as running inside one.
        Assert.Contains("hub doctor isolation", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("changes nothing", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("executions are refused", result.StdOut, StringComparison.Ordinal);
    }
}
