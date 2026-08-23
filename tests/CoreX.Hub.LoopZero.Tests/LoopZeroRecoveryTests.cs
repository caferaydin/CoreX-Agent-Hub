using System.Diagnostics;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// What survives the Hub being killed.
///
/// These tests do not call a reconciler; they start the shipped executable, wait until the
/// database says work is really in flight, and then kill that process from outside. What the
/// next process makes of the wreckage is the thing under test.
/// </summary>
public sealed class LoopZeroRecoveryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Waits until the database says an execution has reached a particular state.</summary>
    private static Task<DomainExecution> WaitForExecutionAsync(
        LoopZeroHub hub,
        Func<DomainExecution, bool> until,
        string what) =>
        LoopZeroHub.WaitForAsync(
            async () =>
            {
                var store = hub.OpenStore();
                var tasks = await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None);

                if (tasks.Count == 0)
                {
                    return null;
                }

                var executions = await store.ListExecutionsForTaskAsync(tasks[0].Id, CancellationToken.None);

                return executions.FirstOrDefault(until);
            },
            Patience,
            what);

    [Fact]
    public async Task Criterion16_killing_the_hub_mid_run_leaves_work_recoverable_and_no_orphan_processes()
    {
        using var hub = LoopZeroHub.Create(agentMode: "hang");
        await hub.SeedReadyTaskAsync();

        using var running = hub.Start("run", "HUB-001");

        var live = await WaitForExecutionAsync(
            hub,
            e => e.State == ExecutionState.RunningAgent && !string.IsNullOrEmpty(e.WorktreePath),
            "the agent to start");

        Assert.True(Directory.Exists(live.WorktreePath));
        Assert.NotNull(live.LeaseProcessId);
        Assert.Equal(running.Id, live.LeaseProcessId);

        var agents = await LoopZeroHub.WaitForAsync(
            () =>
            {
                var mine = LoopZeroHub.OwnFixtureProcesses();
                return Task.FromResult<List<Process>?>(mine.Count > 0 ? [.. mine] : null);
            },
            Patience,
            "the fixture agent process to appear");

        // Kill only the Hub. Its children are in a job object that dies with it, and that is
        // exactly the claim being tested.
        running.Kill(entireProcessTree: false);
        await running.WaitForExitAsync();

        foreach (var agent in agents)
        {
            var exited = agent.WaitForExit(30_000);
            Assert.True(exited, $"agent process {agent.Id} outlived the Hub that owned it");
            agent.Dispose();
        }

        // A fresh process opens the same installation and reconciles what it finds.
        var restarted = await hub.ExecuteAsync("task", "show", "HUB-001");
        Assert.Equal(0, restarted.ExitCode);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        Assert.Equal(ExecutionState.Interrupted, execution.State);
        Assert.Null(execution.ResultCommitSha);
        Assert.Equal(TaskState.Ready, task.State);

        // The worktree holds the only copy of whatever was done, so nothing removed it.
        Assert.True(Directory.Exists(execution.WorktreePath));

        // And the workspace reconciler says so, without deleting anything.
        var reconcile = await hub.ExecuteAsync(
            "workspace", "reconcile", "--org", "hub-dev", "--repo", "hub-sample-repo");

        Assert.Equal(0, reconcile.ExitCode);
        Assert.Contains(Path.GetFileName(execution.WorktreePath), reconcile.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(execution.WorktreePath));

        // Criterion 17, on a state only a real kill produces: an override cannot rescue it.
        var override17 = await hub.ExecuteAsync(
            "accept", "HUB-001#1", "--override", "--reason", "the machine died, let it through");

        Assert.NotEqual(0, override17.ExitCode);
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Killing_the_hub_after_its_result_commit_recovers_forward_without_a_second_commit()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        // Verification blocks, so the kill lands after the result commit exists and before any
        // verification truth was written - the crash window Phase 5C could not close.
        hub.SetTestStepArguments("silent", "120");

        using var running = hub.Start("run", "HUB-001");

        var committed = await WaitForExecutionAsync(
            hub,
            e => e.ResultCommitSha is not null && e.State == ExecutionState.Verifying,
            "the result commit to be made and verification to start");

        var resultCommit = committed.ResultCommitSha!;
        var branch = committed.BranchName;
        var worktree = committed.WorktreePath;

        var historyBefore = hub.RunGitIn(worktree, "log", "--format=%H", branch);

        running.Kill(entireProcessTree: false);
        await running.WaitForExitAsync();

        // The machine is well again: verification finishes normally from here.
        hub.RestoreTestStep();

        // A fresh process recovers it. Nothing runs the agent again and nothing commits again.
        var recovered = await hub.ExecuteAsync("run", "HUB-001");

        Assert.Contains("recover:", recovered.All, StringComparison.Ordinal);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var executions = await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None);

        var execution = Assert.Single(executions, e => e.AttemptNo == 1);

        Assert.Equal(resultCommit, execution.ResultCommitSha);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.Equal(TaskState.AwaitingAcceptance, task.State);

        // Exactly one Hub commit, and the branch is the same history it was.
        Assert.Equal(historyBefore, hub.RunGitIn(worktree, "log", "--format=%H", branch));
        Assert.Equal(resultCommit, hub.RunGitIn(worktree, "rev-parse", branch));

        // Verification really happened this time, and is durable.
        var commands = await store.ListVerificationCommandsAsync(execution.Id, CancellationToken.None);

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.True(c.IsPass));

        var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);
        Assert.Equal(ExecutionStepState.Succeeded, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
        Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Review).State);

        // And a person can still accept it, in yet another process.
        var accept = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.True(accept.ExitCode == 0, accept.Describe());
    }

    [Fact]
    public async Task A_preserved_worktree_from_an_earlier_attempt_is_warned_about_and_never_removed()
    {
        using var hub = LoopZeroHub.Create(agentMode: "out-of-scope-write");
        await hub.SeedReadyTaskAsync();

        var blocked = await hub.ExecuteAsync("run", "HUB-001");
        Assert.NotEqual(0, blocked.ExitCode);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var first = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        Assert.Null(first.ResultCommitSha);
        Assert.True(Directory.Exists(first.WorktreePath));
        Assert.Equal(TaskState.Ready, task.State);

        var second = await hub.ExecuteAsync("run", "HUB-001");

        // Said once, clearly, and about the attempt it concerns.
        var warnings = second.Stdout
            .Split('\n')
            .Where(l => l.Contains("worktree is preserved", StringComparison.Ordinal))
            .ToList();

        Assert.Single(warnings);
        Assert.Contains("attempt 1", warnings[0], StringComparison.Ordinal);
        Assert.Contains(first.WorktreePath, warnings[0], StringComparison.Ordinal);

        // It warned; it did not delete, and it did not refuse.
        Assert.True(Directory.Exists(first.WorktreePath));

        var after = hub.OpenStore();
        var executions = await after.ListExecutionsForTaskAsync(task.Id, CancellationToken.None);
        Assert.Equal(2, executions.Count);
    }

    [Fact]
    public async Task Two_hub_processes_cannot_run_an_execution_at_the_same_time()
    {
        using var hub = LoopZeroHub.Create(agentMode: "hang");
        await hub.SeedReadyTaskAsync();
        await SeedSecondTaskAsync(hub);

        using var first = hub.Start("run", "HUB-001");

        await WaitForExecutionAsync(
            hub,
            e => e.State == ExecutionState.RunningAgent,
            "the first run to reach the agent");

        // A different task, so the database's own one-execution-per-task rule is not what
        // refuses this: the machine-wide claim is.
        var second = await hub.ExecuteAsync("run", "HUB-002");

        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("already running an execution", second.All, StringComparison.OrdinalIgnoreCase);

        var store = hub.OpenStore();
        var other = Assert.Single(await store.FindTasksByKeyAsync("HUB-002", CancellationToken.None));

        Assert.Empty(await store.ListExecutionsForTaskAsync(other.Id, CancellationToken.None));

        LoopZeroHub.TryKillTree(first);
    }

    private static async Task SeedSecondTaskAsync(LoopZeroHub hub)
    {
        await hub.SeedTaskAsync("HUB-002");

        var ready = await hub.CliAsync("task", "ready", "HUB-002");
        Assert.True(ready.ExitCode == 0, ready.Describe());
    }
}
