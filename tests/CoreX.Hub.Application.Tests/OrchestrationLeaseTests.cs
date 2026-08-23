using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Leases, heartbeats and what happens to an execution nobody is driving any more.
/// The clock and process liveness are substituted, so none of this waits on real time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationLeaseTests
{
    [Fact]
    public async Task A_finished_execution_releases_its_lease()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        Assert.True(execution.IsTerminal);
        Assert.Null(execution.LeaseProcessId);
        Assert.Null(execution.LeaseHeartbeatUtc);

        // A terminal execution is never stale, so the reconciler leaves it alone.
        Assert.False(execution.IsLeaseStale(false, hub.Clock.GetUtcNow().AddDays(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_dead_owner_makes_the_execution_interrupted_and_the_task_available()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        // An execution that was claimed and then abandoned mid pipeline.
        var execution = await AbandonAsync(hub);

        hub.Liveness.OwnerIsAlive = false;

        var report = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        var reconciled = Assert.Single(report.Interrupted);
        Assert.Equal(execution.Id, reconciled.ExecutionId);
        Assert.Contains("gone", reconciled.Reason, StringComparison.Ordinal);

        var afterwards = await hub.ExecutionAsync(execution.Id);
        Assert.Equal(ExecutionState.Interrupted, afterwards.State);
        Assert.Null(afterwards.Outcome);

        // No result commit, so the worktree is preserved and the task can be tried again.
        Assert.True(afterwards.WorktreeMustBePreserved);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task A_silent_owner_is_treated_as_abandoned_even_while_it_appears_alive()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var execution = await AbandonAsync(hub);

        // The process is still there, but it has not said anything for far too long.
        hub.Liveness.OwnerIsAlive = true;
        hub.Clock.Advance(TimeSpan.FromMinutes(10));

        var report = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Single(report.Interrupted);
        Assert.Contains("stopped reporting", report.Interrupted[0].Reason, StringComparison.Ordinal);
        Assert.Equal(ExecutionState.Interrupted, (await hub.ExecutionAsync(execution.Id)).State);
    }

    [Fact]
    public async Task A_recycled_process_id_is_not_the_original_owner()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var execution = await AbandonAsync(hub);

        // Same number, different program: the host start time is what tells them apart.
        hub.Liveness.OwnerIsAlive = true;
        hub.Liveness.CurrentHostStartedAtUtc = hub.Liveness.CurrentHostStartedAtUtc.AddHours(3);

        var report = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Single(report.Interrupted);
        Assert.Contains("gone", report.Interrupted[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_live_owner_that_is_still_reporting_is_left_alone()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        await AbandonAsync(hub);

        hub.Liveness.OwnerIsAlive = true;
        hub.Clock.Advance(TimeSpan.FromSeconds(5));

        var report = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        Assert.Empty(report.Interrupted);
    }

    /// <summary>
    /// Leaves a claimed, non terminal execution behind, exactly as a crash would.
    /// </summary>
    private static async Task<Domain.Executions.Execution> AbandonAsync(OrchestrationHarness hub)
    {
        var now = hub.Clock.GetUtcNow();
        var task = await hub.TaskAsync();

        var execution = new Domain.Executions.Execution(
            Guid.CreateVersion7(),
            hub.OrganizationId,
            hub.TaskId,
            1,
            "1111111111111111111111111111111111111111",
            "agent/HUB-001/1",
            Path.Combine(hub.WorkspaceRoot, "abandoned"),
            null,
            now);

        execution.Advance(ExecutionState.RunningAgent, now);
        execution.ClaimLease(hub.Liveness.CurrentProcessId, hub.Liveness.CurrentHostStartedAtUtc, now);
        task.MarkRunning(now);

        hub.Store.AddExecution(execution);
        await hub.Store.SaveChangesAsync(CancellationToken.None);

        return execution;
    }
}
