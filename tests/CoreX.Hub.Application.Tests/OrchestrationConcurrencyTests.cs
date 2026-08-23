using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// One execution at a time on this machine, and never two live executions for one task.
/// Both rules are local and narrow: no queue, no distributed lock, no worker cluster.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationConcurrencyTests
{
    [Fact]
    public async Task Two_requests_do_not_run_at_the_same_time()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var orchestrator = hub.Orchestrator();

        var first = orchestrator.RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);
        var second = orchestrator.RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        // The second waits for the first rather than running beside it, and by the time it
        // is admitted the task is no longer Ready - so it is refused instead.
        var refused = await Assert.ThrowsAsync<ExecutionStartException>(async () =>
        {
            await Task.WhenAll(first, second);
        });

        Assert.Contains("not Ready", refused.Message, StringComparison.Ordinal);

        var executions = await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None);
        Assert.Single(executions);
    }

    [Fact]
    public async Task A_task_with_a_live_execution_cannot_start_another()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var now = hub.Clock.GetUtcNow();

        var running = new Domain.Executions.Execution(
            Guid.CreateVersion7(), hub.OrganizationId, hub.TaskId, 1,
            "1111111111111111111111111111111111111111", "agent/HUB-001/1",
            Path.Combine(hub.WorkspaceRoot, "busy"), null, now);

        running.Advance(ExecutionState.RunningAgent, now);

        // The task is deliberately left Ready, so the duplicate execution guard is the one
        // under test rather than the task state check that would normally fire first.
        hub.Store.AddExecution(running);
        await hub.Store.SaveChangesAsync(CancellationToken.None);

        var refused = await Assert.ThrowsAsync<ExecutionStartException>(() =>
            hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        Assert.Contains("non terminal execution", refused.Message, StringComparison.Ordinal);
        Assert.Single(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));
    }

    [Fact]
    public async Task A_task_that_is_not_ready_cannot_be_executed()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        // The task is now waiting for a human, so it is not available to run again.
        var refused = await Assert.ThrowsAsync<ExecutionStartException>(() =>
            hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        Assert.Contains("AwaitingAcceptance", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_agent_profile_is_refused_before_anything_is_created()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        await Assert.ThrowsAsync<ExecutionStartException>(() =>
            hub.Orchestrator().RunAsync(hub.TaskId, "not-a-profile", CancellationToken.None));

        Assert.Empty(await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));
    }
}
