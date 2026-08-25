using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Cancellation that lands inside a write, which is the one window STAB-01 left open.
///
/// A transition is two things that are not the same moment: the aggregate is mutated, and
/// then the row is saved. Cancellation between them leaves a tracked instance that says
/// terminal and a database row that says otherwise - and the identity map hands the mutated
/// instance back to anybody who asks the same context, so the divergence is invisible from
/// where it was created.
///
/// Everything here is about one rule: after an interrupted write, the database is the
/// authority. What the tracked aggregate believes is a memory of an intention, not a fact.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DurableCancellationTests
{
    /// <summary>
    /// The divergence itself, stated before anything is asked to survive it.
    ///
    /// It matters as its own test because the bug it enables is not a rare interleaving: it
    /// is what an ordinary `if (execution.IsTerminal) return;` would read, and that shortcut
    /// looks obviously correct right up until the save behind it was cancelled.
    /// </summary>
    [Fact]
    public async Task A_cancelled_save_leaves_the_tracked_aggregate_ahead_of_the_database()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var seam = Interrupting(hub, cancellation, commitFirst: false);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(seam.Store!.Interrupted, "the seam must have caught a save");

        // Two readers, two answers. The tracked one is the instance the pipeline mutated on
        // its way to a Failed row that was never written.
        var tracked = await hub.Store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

        Assert.NotNull(tracked);
        Assert.True(tracked!.IsTerminal);
        Assert.Equal(ExecutionState.Failed, tracked.State);

        // The database says something else, and the finalizer followed the database. Had it
        // read the tracked instance instead - by transitioning it again, or by checking it
        // and returning early - this row would say Failed or nothing at all.
        var durable = await Durable(hub, report.ExecutionId);

        Assert.Equal(ExecutionState.Cancelled, durable.State);
        Assert.NotEqual(tracked.State, durable.State);
    }

    /// <summary>
    /// The defect the real Phase 7 reviewer found, as behaviour.
    ///
    /// Before the fix this run ended with a DomainException in place of the cancellation and
    /// a non terminal row in the database - an execution the next process had to reconcile,
    /// which is exactly what STAB-01 set out to make unnecessary.
    /// </summary>
    [Fact]
    public async Task A_cancellation_inside_a_terminal_write_still_ends_the_execution_durably()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var seam = Interrupting(hub, cancellation, commitFirst: false);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(seam.Store!.Interrupted);
        Assert.True(seam.Workspace!.PrepareAttempted);

        // No DomainException in place of the cancellation, and the run reports what really
        // happened rather than the transition that never landed.
        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.Outcome);
        Assert.Null(report.ResultCommitSha);

        var durable = await Durable(hub, report.ExecutionId);

        Assert.Equal(ExecutionState.Cancelled, durable.State);
        Assert.Null(durable.Outcome);
        Assert.Null(durable.ResultCommitSha);
        Assert.True(durable.IsTerminal);

        // Caller cancellation hands the work back to the queue, durably - read through a
        // store that never saw the abandoned mutation.
        var durableTask = await hub.OpenStore().FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.Equal(TaskState.Ready, durableTask!.State);
    }

    /// <summary>
    /// The other half of the ambiguity: the save reached the database and the answer was
    /// lost on the way back.
    ///
    /// Nothing in the exception distinguishes this from the case above, so the finalizer may
    /// not infer one from it. Here the durable row is already terminal, and the only correct
    /// action is to leave it alone.
    /// </summary>
    [Fact]
    public async Task A_save_that_committed_before_the_cancellation_is_never_terminalized_twice()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var seam = Interrupting(hub, cancellation, commitFirst: true);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(seam.Store!.Interrupted);

        // The transition that did commit was Failed - the workspace really could not be
        // prepared - and machine truth is not rewritten into Cancelled to match the caller.
        var durable = await Durable(hub, report.ExecutionId);

        Assert.Equal(ExecutionState.Failed, durable.State);
        Assert.Equal(ExecutionState.Failed, report.State);
        Assert.Null(durable.ResultCommitSha);

        // Exactly one terminal transition was written for this execution.
        var audits = (await hub.AuditEventsAsync(report.ExecutionId))
            .Where(a => a.EventType is "ExecutionTerminated" or "ExecutionCompleted")
            .ToList();

        Assert.Single(audits);

        // And the task is back in the queue, read from a process that shares nothing.
        var store = await hub.ReopenStoreAsync();
        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.Equal(TaskState.Ready, task!.State);
    }

    /// <summary>
    /// A normal cancellation must not need a restart to be finished, and must not look like
    /// something for the reconciler to repair afterwards.
    /// </summary>
    [Fact]
    public async Task A_finalized_cancellation_needs_no_reconciler_repair_in_a_new_process()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        Interrupting(hub, cancellation, commitFirst: false);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        // A different process, a different connection, nothing carried over.
        var store = await hub.ReopenStoreAsync();

        var before = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

        Assert.True(before!.IsTerminal);
        Assert.Equal(ExecutionState.Cancelled, before.State);
        Assert.Equal(TaskState.Ready, (await store.FindTaskAsync(hub.TaskId, CancellationToken.None))!.State);

        var reconciled = await new StaleExecutionReconciler(
            store, hub.Liveness, hub.Clock, new ExecutionOrchestratorOptions())
            .ReconcileAsync(CancellationToken.None);

        Assert.Empty(reconciled.Interrupted);
        Assert.Empty(reconciled.RecoveryRequired);
    }

    /// <summary>
    /// The operator race: a stop request arrives, the write that records it is interrupted,
    /// and the task must still only be abandoned once the execution really ended.
    /// </summary>
    [Fact]
    public async Task An_operator_stop_whose_write_was_interrupted_still_ends_with_a_cancelled_task()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        Interrupting(hub, cancellation, commitFirst: false);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        var store = await hub.ReopenStoreAsync();

        // The operator's own service is the authority for abandoning the task, and it only
        // acts once the execution has durably ended - which, after the fix, it has.
        var execution = await store.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

        Assert.True(execution!.IsTerminal);

        var service = new TaskCancellationService(hub.OpenStore, new NoListener(), hub.Clock);

        var result = await service.CancelAsync(
            (await store.FindTaskAsync(hub.TaskId, CancellationToken.None))!.TaskKey.Value,
            null,
            "operator stopped it",
            CancellationToken.None);

        Assert.NotNull(result);

        var after = await hub.ReopenStoreAsync();

        Assert.Equal(TaskState.Cancelled, (await after.FindTaskAsync(hub.TaskId, CancellationToken.None))!.State);
    }

    /// <summary>
    /// Arms the harness so the save carrying a terminal transition is interrupted.
    ///
    /// The workspace is made to fail, which sends the pipeline down its shortest terminal
    /// path: one save closing the PrepareWorkspace step, then the save carrying
    /// <c>Execution.Terminate</c>. The seam skips the first and catches the second, and every
    /// test that uses it asserts afterwards which transition was actually caught.
    /// </summary>
    private static Seam Interrupting(
        OrchestrationHarness hub,
        CancellationTokenSource cancellation,
        bool commitFirst)
    {
        // A holder rather than locals, because the decorators do not run until the
        // orchestrator is built - which is after this method has returned.
        var seam = new Seam();

        hub.DecorateStore = inner => seam.Store = new InterruptedStore(inner)
        {
            CommitBeforeThrowing = commitFirst,
            OnInterrupt = cancellation.Cancel,
        };

        hub.DecorateWorkspace = inner => seam.Workspace = new ScriptedWorkspace(inner)
        {
            RefusePreparation = true,

            // Armed here rather than earlier: from this point the next two saves are the
            // step closing and the terminal transition, in that order.
            BeforePrepare = () => seam.Store!.InterruptAfter(1),
        };

        return seam;
    }

    private sealed class Seam
    {
        public InterruptedStore? Store { get; set; }

        public ScriptedWorkspace? Workspace { get; set; }
    }

    private static async Task<DomainExecution> Durable(OrchestrationHarness hub, Guid executionId)
    {
        var store = hub.OpenStore();

        return await store.FindExecutionAsync(executionId, CancellationToken.None)
               ?? throw new InvalidOperationException("the execution row disappeared");
    }

    private sealed class NoListener : IExecutionCancellationSignal
    {
        public IDisposable Listen(Guid executionId, Action onRequested) => new Nothing();

        public bool TryRequest(Guid executionId) => false;

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
