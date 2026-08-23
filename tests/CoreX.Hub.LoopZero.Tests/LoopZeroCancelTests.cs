using System.Diagnostics;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Stopping work, and the difference between asking and having stopped it.
///
/// An operator cancelling a task is not the same event as an execution ending Cancelled. The
/// first is "this work is not wanted"; the second is "this attempt was stopped". They can be
/// true together, and the order matters: a task that says Cancelled while an agent is still
/// writing into a worktree would be a record that contradicts the machine.
/// </summary>
public sealed class LoopZeroCancelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

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
    public async Task Cancelling_a_ready_task_ends_it_without_inventing_an_execution()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        var cancelled = await hub.ExecuteAsync("cancel", "HUB-001", "--reason", "not wanted after all");

        Assert.True(cancelled.ExitCode == 0, cancelled.Describe());
        Assert.Contains("Cancelled", cancelled.Stdout, StringComparison.Ordinal);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.Empty(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        // The decision is on the record as the operator's.
        var audits = await hub.ReadAuditAsync(task.Id);
        Assert.Contains(audits, a => a.EventType == TaskCancellationService.AuditType);

        // A second cancellation has nothing left to do and says so.
        var again = await hub.ExecuteAsync("cancel", "HUB-001");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("already Cancelled", again.Stdout + again.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_running_task_stops_the_work_first_and_only_then_records_the_decision()
    {
        using var hub = LoopZeroHub.Create(agentMode: "hang");
        await hub.SeedReadyTaskAsync();

        using var running = hub.Start("run", "HUB-001");

        var live = await WaitForExecutionAsync(
            hub,
            e => e.State == ExecutionState.RunningAgent,
            "the agent to start");

        var agents = await LoopZeroHub.WaitForAsync(
            () =>
            {
                var mine = LoopZeroHub.OwnFixtureProcesses();
                return Task.FromResult<List<Process>?>(mine.Count > 0 ? [.. mine] : null);
            },
            Patience,
            "the fixture agent process to appear");

        // A different process asks this exact execution to stop.
        var cancelled = await hub.ExecuteAsync(TimeSpan.FromMinutes(2), "cancel", "HUB-001");

        Assert.True(cancelled.ExitCode == 0, cancelled.Describe());
        Assert.Contains(live.Id.ToString("D"), cancelled.Stdout, StringComparison.Ordinal);

        // The run really finished, and it finished by being stopped.
        Assert.True(running.WaitForExit(60_000), "the run process did not exit");

        foreach (var agent in agents)
        {
            Assert.True(agent.WaitForExit(30_000), $"agent process {agent.Id} outlived the cancellation");
            agent.Dispose();
        }

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        Assert.Equal(ExecutionState.Cancelled, execution.State);
        Assert.Null(execution.ResultCommitSha);
        Assert.Equal(TaskState.Cancelled, task.State);

        // The mandatory capture still ran: a stopped run still owes an account of itself.
        var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);
        Assert.Equal(
            ExecutionStepState.Succeeded,
            steps.Single(s => s.Kind == ExecutionStepKind.CaptureChanges).State);

        Assert.NotEmpty(await store.ListEvidenceAsync(execution.Id, CancellationToken.None));

        // And the worktree is preserved, because no commit was made.
        Assert.True(Directory.Exists(execution.WorktreePath));
    }

    [Fact]
    public async Task Cancelling_after_the_run_has_finished_ends_the_task_without_touching_what_it_produced()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        var run = await hub.ExecuteAsync("run", "HUB-001");
        Assert.True(run.ExitCode == 0, run.Describe());

        var cancelled = await hub.ExecuteAsync("cancel", "HUB-001", "--reason", "changed my mind");
        Assert.True(cancelled.ExitCode == 0, cancelled.Describe());

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        // One authoritative answer on each side, and they do not contradict each other: the
        // machine measured a passing run, and a person decided not to take it.
        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.NotNull(execution.ResultCommitSha);

        // Cancellation is not a decision about the work, so none was recorded.
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));

        // And acceptance is out of reach from here.
        var accept = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.NotEqual(0, accept.ExitCode);
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_during_verification_never_produces_a_cancelled_execution_holding_a_commit()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        // Verification blocks, so the cancellation arrives after the result commit exists.
        hub.SetTestStepArguments("silent", "120");

        using var running = hub.Start("run", "HUB-001");

        var committed = await WaitForExecutionAsync(
            hub,
            e => e.ResultCommitSha is not null && e.State == ExecutionState.Verifying,
            "verification to start on a committed result");

        var resultCommit = committed.ResultCommitSha!;

        var cancelled = await hub.ExecuteAsync(TimeSpan.FromMinutes(2), "cancel", "HUB-001");
        Assert.True(cancelled.ExitCode == 0, cancelled.Describe());

        Assert.True(running.WaitForExit(60_000), "the run process did not exit");

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        // The frozen invariant: no terminal Cancelled execution carries a result commit.
        Assert.NotEqual(ExecutionState.Cancelled, execution.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, execution.Outcome);
        Assert.Equal(resultCommit, execution.ResultCommitSha);

        // The commit is still in the repository, unrewritten.
        Assert.Equal(resultCommit, hub.RunGitIn(execution.WorktreePath, "rev-parse", execution.BranchName));

        // The operator's decision stands on the task, not on the machine truth.
        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));

        var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);
        Assert.Equal(ExecutionStepState.Cancelled, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
    }

    [Fact]
    public async Task Asking_to_cancel_an_execution_nobody_is_running_changes_nothing()
    {
        using var hub = LoopZeroHub.Create(agentMode: "hang");
        await hub.SeedReadyTaskAsync();

        using var running = hub.Start("run", "HUB-001");

        var live = await WaitForExecutionAsync(
            hub,
            e => e.State == ExecutionState.RunningAgent,
            "the agent to start");

        // The host dies without telling anyone. Nothing is listening for this execution any
        // more, so a cancellation cannot stop it - and must not pretend to.
        running.Kill(entireProcessTree: true);
        await running.WaitForExitAsync();

        var cancelled = await hub.ExecuteAsync("cancel", "HUB-001");

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        // Either the session's own reconciliation had already closed the abandoned execution -
        // in which case there was nothing running and the task is cancelled - or it had not,
        // in which case the request found no listener and refused. Both are honest; what is
        // forbidden is a cancelled task over an execution still recorded as running.
        if (cancelled.ExitCode == 0)
        {
            Assert.Equal(TaskState.Cancelled, task.State);

            var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));
            Assert.True(execution.IsTerminal);
        }
        else
        {
            Assert.NotEqual(TaskState.Cancelled, task.State);
            Assert.Contains("listening", cancelled.All, StringComparison.OrdinalIgnoreCase);
        }

        _ = live;
    }
}
