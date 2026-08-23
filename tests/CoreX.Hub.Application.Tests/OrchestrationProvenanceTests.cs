using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Where an execution's base commit comes from, and what it survives.
///
/// The whole pipeline downstream treats that commit as fact: the context pack is built from
/// it, the captured diff is measured against it, the baseline is cached under it. So it has
/// to be a commit the repository actually answered with, it has to be that from the first
/// row written, and nothing that happens afterwards - a branch moving, a workspace claiming
/// something else, a host dying - may change it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationProvenanceTests
{
    private const string NullObjectId = "0000000000000000000000000000000000000000";

    [Fact]
    public async Task An_execution_is_based_on_a_commit_the_repository_answered_with()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        Assert.Equal(x, execution.BaseCommitSha);
        Assert.NotEqual(NullObjectId, execution.BaseCommitSha);

        // Not merely well shaped: git resolves it, and it is the commit the branch pointed
        // at when the attempt started.
        Assert.Equal(x, hub.RunGit("rev-parse", execution.BaseCommitSha + "^{commit}"));
    }

    [Fact]
    public async Task A_branch_that_moves_after_pinning_changes_nothing_about_the_execution()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // X: what the repository answers with when the attempt starts.
        var x = hub.RunGit("rev-parse", "HEAD");

        ScriptedWorkspace? scripted = null;

        hub.DecorateWorkspace = inner => scripted = new ScriptedWorkspace(inner)
        {
            // Y: main advances in the window between pinning the commit and creating the
            // worktree. This is the race the handoff exists for.
            AfterPreflight = () =>
            {
                File.WriteAllText(Path.Combine(hub.RepositoryPath, "src", "moved.cs"), "// moved on\n");
                hub.RunGit("add", "-A");
                hub.RunGit("commit", "-q", "-m", "main advanced while the execution was starting");
            },
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var y = hub.RunGit("rev-parse", "main");

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.Equal(x, scripted!.PinnedBaseCommitSha);
        Assert.NotEqual(x, y);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        // The execution is based on X, and the agent's tree was X: the commit the Hub made
        // sits directly on it, and the file that only exists on Y is nowhere in the tree.
        Assert.Equal(x, execution.BaseCommitSha);
        Assert.Equal(x, scripted.CapturedHeadSha);
        Assert.Equal(x, hub.RunGit("rev-parse", report.ResultCommitSha + "^"));
        Assert.False(File.Exists(Path.Combine(execution.WorktreePath, "src", "moved.cs")));

        // The baseline the run compared against is X's as well, not the branch's.
        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        var baseline = Assert.Single(events, e => e.EventType == "Baseline");

        Assert.Contains(x[..12], baseline.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(y[..12], baseline.Message, StringComparison.Ordinal);

        // Z: the branch moves again once everything is over, and the record does not.
        File.WriteAllText(Path.Combine(hub.RepositoryPath, "src", "later.cs"), "// later\n");
        hub.RunGit("add", "-A");
        hub.RunGit("commit", "-q", "-m", "later work on main");

        var z = hub.RunGit("rev-parse", "main");

        Assert.NotEqual(y, z);
        Assert.Equal(x, (await hub.ExecutionAsync(report.ExecutionId)).BaseCommitSha);
    }

    [Fact]
    public async Task A_workspace_that_reports_a_different_base_commit_fails_closed()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");

        hub.DecorateWorkspace = inner => new ScriptedWorkspace(inner)
        {
            ReportPreparedBaseAs = "abcdef0123456789abcdef0123456789abcdef01",
        };

        await Assert.ThrowsAsync<DomainException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        // The execution keeps the commit it was created with. Adopting the workspace's
        // answer would have moved the provenance a baseline is already keyed on.
        var executions = await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None);
        var execution = Assert.Single(executions);

        Assert.Equal(x, execution.BaseCommitSha);
        Assert.Null(execution.ResultCommitSha);
    }

    [Fact]
    public async Task A_crash_before_the_worktree_exists_leaves_the_real_commit_behind()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");

        hub.DecorateWorkspace = inner => new ScriptedWorkspace(inner)
        {
            FailPreparation = new InvalidOperationException("the host died before the worktree existed"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        // The row already exists, because the commit was resolved before it was written.
        var started = Assert.Single(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        Assert.Equal(x, started.BaseCommitSha);
        Assert.False(started.IsTerminal);

        // Reconciliation closes it honestly, and changes nothing about what it was based on.
        hub.Liveness.OwnerIsAlive = false;

        var reconciled = await hub.Reconciler().ReconcileAsync(CancellationToken.None);
        Assert.Single(reconciled.Interrupted);

        var execution = await hub.ExecutionAsync(started.Id);

        Assert.Equal(ExecutionState.Interrupted, execution.State);
        Assert.Equal(x, execution.BaseCommitSha);
        Assert.NotEqual(NullObjectId, execution.BaseCommitSha);
        Assert.Null(execution.ResultCommitSha);

        // No worktree was invented for a worktree that never existed.
        Assert.False(Directory.Exists(Path.Combine(hub.WorkspaceRoot, "hub-dev", "hub-sample", "HUB-001-1")));
        Assert.False(Directory.Exists(execution.WorktreePath));

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }
}
