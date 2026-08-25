using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Cancellation that lands inside the write which claims the execution.
///
/// The claim is one transaction: the execution row appears, the task becomes Running, and
/// an audit row records it. Until it commits there is nothing to account for; the moment it
/// commits there is an attempt that owes an account of itself.
///
/// An OperationCanceledException from that save says nothing about which side of the commit
/// it landed on. So the two cases are told apart the only way they can be - by asking the
/// database - and never by reading the exception or the instances the save was carrying.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutionStartCancellationTests
{
    [Fact]
    public async Task A_start_that_never_committed_leaves_no_attempt_behind()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var seam = Interrupting(hub, cancellation, commitFirst: false);

        // Nothing to report: no execution was claimed, so the cancellation is simply the
        // answer.
        await Assert.ThrowsAsync<OperationCanceledException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token));

        Assert.True(seam.Store!.Interrupted);

        var durable = hub.OpenStore();

        Assert.Empty(await durable.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        // And the task never left the queue.
        var task = await durable.FindTaskAsync(hub.TaskId, CancellationToken.None);
        Assert.Equal(TaskState.Ready, task!.State);
    }

    /// <summary>
    /// The window the STAB-02 residual named: the claim reached the database and the answer
    /// was lost on the way back, so an attempt exists that the pipeline never owned.
    ///
    /// Leaving it Running would be a row the next process has to reconcile - which is the
    /// exact condition an ordinary cancellation is supposed to stop producing.
    /// </summary>
    [Fact]
    public async Task A_start_that_committed_before_the_cancellation_still_ends_the_attempt()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var seam = Interrupting(hub, cancellation, commitFirst: true);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(seam.Store!.Interrupted);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.Outcome);
        Assert.Null(report.ResultCommitSha);

        var durable = hub.OpenStore();

        var execution = Assert.Single(await durable.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        Assert.True(execution.IsTerminal);
        Assert.Equal(ExecutionState.Cancelled, execution.State);
        Assert.Null(execution.ResultCommitSha);

        Assert.Equal(
            TaskState.Ready,
            (await durable.FindTaskAsync(hub.TaskId, CancellationToken.None))!.State);
    }

    /// <summary>Nothing external is reached: the attempt ended before it began.</summary>
    [Fact]
    public async Task A_start_cancelled_this_way_never_prepares_a_worktree_or_runs_an_agent()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        ScriptedWorkspace? workspace = null;
        hub.DecorateWorkspace = inner => workspace = new ScriptedWorkspace(inner);

        Interrupting(hub, cancellation, commitFirst: true);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.False(workspace!.PrepareAttempted);

        var steps = await hub.DurableStepsAsync(report.ExecutionId);

        Assert.DoesNotContain(steps, s => s.State == ExecutionStepState.Running);
        Assert.DoesNotContain(steps, s => s.State == ExecutionStepState.Succeeded);
    }

    /// <summary>An attempt closed this way is finished; a new process finds nothing to repair.</summary>
    [Fact]
    public async Task A_start_cancelled_this_way_needs_no_reconciler_repair()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        Interrupting(hub, cancellation, commitFirst: true);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        var store = await hub.ReopenStoreAsync();

        Assert.True((await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None))!.IsTerminal);

        var reconciled = await new StaleExecutionReconciler(
            store, hub.Liveness, hub.Clock, new ExecutionOrchestratorOptions())
            .ReconcileAsync(CancellationToken.None);

        Assert.Empty(reconciled.Interrupted);
        Assert.Empty(reconciled.RecoveryRequired);
    }

    /// <summary>
    /// The operator's route through the same window: the task is only abandoned once the
    /// execution has really ended, and the service that owns that decision still owns it.
    /// </summary>
    [Fact]
    public async Task An_operator_can_abandon_a_task_whose_start_was_cancelled_this_way()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        Interrupting(hub, cancellation, commitFirst: true);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        var store = await hub.ReopenStoreAsync();

        // Durably terminal first - that is the precondition, not an afterthought.
        Assert.True((await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None))!.IsTerminal);

        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        var result = await new TaskCancellationService(hub.OpenStore, null, hub.Clock)
            .CancelAsync(task!.TaskKey.Value, null, "operator stopped it", CancellationToken.None);

        Assert.NotNull(result);

        var after = await hub.ReopenStoreAsync();

        Assert.Equal(
            TaskState.Cancelled,
            (await after.FindTaskAsync(hub.TaskId, CancellationToken.None))!.State);
    }

    /// <summary>
    /// Arms the seam on the very first save an execution makes.
    ///
    /// That save is the start transaction: validation only reads, and the workspace preflight
    /// touches no store at all. Every test here asserts afterwards what the database actually
    /// holds, so an arming mistake fails loudly rather than passing vacuously.
    /// </summary>
    private static Seam Interrupting(
        OrchestrationHarness hub,
        CancellationTokenSource cancellation,
        bool commitFirst)
    {
        // A holder rather than a local, because the decorator does not run until the
        // orchestrator is built - which is after this method has returned.
        var seam = new Seam();

        hub.DecorateStore = inner =>
        {
            seam.Store = new InterruptedStore(inner)
            {
                CommitBeforeThrowing = commitFirst,
                OnInterrupt = cancellation.Cancel,
            };

            seam.Store.InterruptAfter(0);

            return seam.Store;
        };

        return seam;
    }

    private sealed class Seam
    {
        public InterruptedStore? Store { get; set; }
    }
}
