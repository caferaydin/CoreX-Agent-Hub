using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Cancellation before the agent, which is the window the Phase 6 acceptance found open.
///
/// Everything from the moment the execution row exists until the agent returns used to run
/// on the caller's token alone, including the writes that record what the run did. So a
/// stop in that window did not end the execution - it threw out of RunAsync and left a row
/// mid pipeline for the next process to reconcile. A Hub that is still running and has
/// already seen the cancellation must not need a restart to finish it.
///
/// The rule these pin down: cancelling stops the work and still owes an account of it. The
/// account is Cancelled, never Failed, and it carries no result commit.
///
/// No sleeps. Each moment is reached through the port that owns it, so the test presses
/// stop exactly where it means to however fast or slow the machine is.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EarlyCancellationTests
{
    [Fact]
    public async Task Cancelling_immediately_after_the_execution_row_exists_still_terminalizes()
    {
        // The earliest moment an execution can be cancelled at all: the row has just been
        // written durably, and nothing external has happened yet.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        ScriptedWorkspace? workspace = null;
        hub.DecorateWorkspace = inner => workspace = new ScriptedWorkspace(inner);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId,
            OrchestrationHarness.ScriptedProfileName,
            cancellation.Token,
            onExecutionStarted: _ => cancellation.Cancel());

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.Outcome);
        Assert.Null(report.ResultCommitSha);

        // Nothing external was reached.
        Assert.False(workspace!.PrepareAttempted);

        await AssertClosedAsync(hub, report, expectedRunningStep: null);
    }

    [Fact]
    public async Task Cancelling_while_the_workspace_is_being_prepared_still_terminalizes()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        ScriptedWorkspace? workspace = null;
        hub.DecorateWorkspace = inner => workspace = new ScriptedWorkspace(inner)
        {
            BeforePrepare = cancellation.Cancel,
        };

        ScriptedContextBuilder? context = null;
        hub.DecorateContext = inner => context = new ScriptedContextBuilder(inner);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.ResultCommitSha);

        Assert.True(workspace!.PrepareAttempted);

        // The pipeline stopped there: no context, and therefore no agent.
        Assert.False(context!.BuildAttempted);

        await AssertClosedAsync(hub, report, ExecutionStepKind.PrepareWorkspace);
    }

    [Fact]
    public async Task Cancelling_while_the_context_is_being_built_never_launches_the_agent()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        var agentLaunched = false;
        hub.DecorateAgent = inner => new AnnouncingAgentGateway(inner, () => agentLaunched = true);

        ScriptedContextBuilder? context = null;
        hub.DecorateContext = inner => context = new ScriptedContextBuilder(inner)
        {
            BeforeBuild = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.ResultCommitSha);

        Assert.True(context!.BuildAttempted);
        Assert.False(agentLaunched);

        await AssertClosedAsync(hub, report, ExecutionStepKind.BuildContext);
    }

    [Fact]
    public async Task A_worktree_that_already_exists_is_preserved_when_the_run_is_cancelled()
    {
        // ADR-0016: no result commit means the worktree holds the only copy of whatever was
        // done, so cancelling never releases or cleans it.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        hub.DecorateContext = inner => new ScriptedContextBuilder(inner)
        {
            BeforeBuild = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        Assert.True(execution.WorktreeMustBePreserved);
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);
        Assert.True(Directory.Exists(execution.WorktreePath));
    }

    [Fact]
    public async Task Cancelling_before_the_execution_exists_creates_no_execution_at_all()
    {
        // The other side of the boundary. Until the start transaction has committed there is
        // nothing to cancel, and inventing an execution purely to record that would put a row
        // in the database for work that never began.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        hub.DecorateWorkspace = inner => new ScriptedWorkspace(inner)
        {
            AfterPreflight = cancellation.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token));

        // Read durably, on a fresh connection. The start transaction mutates the task and
        // adds the execution in memory before it saves, so the context that ran the attempt
        // still holds those unsaved edits; what a later process would see is the only
        // statement about the boundary worth making.
        var reopened = await hub.ReopenStoreAsync();

        Assert.Empty(await reopened.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        var task = await reopened.FindTaskAsync(hub.TaskId, CancellationToken.None);

        Assert.Equal(TaskState.Ready, task!.State);
    }

    [Fact]
    public async Task An_execution_cancelled_early_is_already_terminal_when_the_database_is_reopened()
    {
        // The reconciler is a crash fallback. A Hub process that saw the cancellation and
        // exited normally must not leave it anything to repair.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        hub.DecorateContext = inner => new ScriptedContextBuilder(inner)
        {
            BeforeBuild = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);

        // A different connection, as a later process would see it.
        var reopened = await hub.ReopenStoreAsync();

        var execution = (await reopened.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None)).Single();

        Assert.True(execution.IsTerminal);
        Assert.Equal(ExecutionState.Cancelled, execution.State);
        Assert.Null(execution.ResultCommitSha);

        var reconciler = new StaleExecutionReconciler(
            hub.OpenStore(), hub.Liveness, hub.Clock, new ExecutionOrchestratorOptions());
        var reconciled = await reconciler.ReconcileAsync(CancellationToken.None);

        // Nothing to repair, because the run already closed itself.
        Assert.Empty(reconciled.Interrupted);
        Assert.Empty(reconciled.RecoveryRequired);
    }

    [Fact]
    public async Task A_cancelled_run_writes_exactly_one_terminal_result()
    {
        // Cancellation racing the pipeline must not produce two transitions, and must not
        // leave the task Running behind a terminal execution.
        //
        // Stated as counts rather than staged through a second seam. An earlier version armed
        // both the workspace and the context builder, but the first stops the run before the
        // second is ever reached - the sibling test asserts exactly that - so the second seam
        // proved nothing. What it meant to say is asserted here directly.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var cancellation = new CancellationTokenSource();

        hub.DecorateWorkspace = inner => new ScriptedWorkspace(inner)
        {
            BeforePrepare = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);

        // One terminal transition, written once.
        var terminal = (await hub.AuditEventsAsync(report.ExecutionId))
            .Where(a => a.EventType is "ExecutionTerminated" or "ExecutionCompleted")
            .ToList();

        Assert.Single(terminal);

        var execution = await hub.DurableExecutionAsync(report.ExecutionId);

        Assert.Equal(ExecutionState.Cancelled, execution.State);
        Assert.Null(execution.Outcome);
        Assert.Equal(TaskState.Ready, (await hub.DurableTaskAsync()).State);
    }

    [Fact]
    public async Task An_operator_cancelling_before_the_agent_stops_the_work_and_then_ends_the_task()
    {
        // The operator path, end to end, through the real named signal and the real
        // cancellation service - the same two pieces the command line wires together.
        //
        // The listener is registered the moment the execution row exists, which is before
        // PreparingWorkspace, so an operator can reach a run that has not got to the agent
        // yet. What used to happen then is the defect: the runner threw, never wrote a
        // terminal execution, and the service waited out its deadline and refused to record
        // the decision. The order it insists on is the point - work stops first, the task is
        // ended only once the database says so.
        await using var hub = await OrchestrationHarness.CreateAsync();

        using var stopping = new CancellationTokenSource();
        var signal = new NamedExecutionCancellation();

        var service = new TaskCancellationService(hub.OpenStore, signal, TimeProvider.System);

        Task<TaskCancellationResult>? operatorDecision = null;

        // The request travels through a named OS event and a thread pool wait, so it arrives
        // some time after CancelAsync is called. Nothing used to make the pipeline wait for
        // it, which left "the agent never launched" depending on which of the two won - and a
        // slower machine or a faster context build would have flipped the result.
        //
        // So the seam waits for the stop it is supposed to observe. Bounded, because a signal
        // that never arrives is a failure rather than something to hang on.
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fired inside the run, and deliberately not awaited there: the service waits for
        // this very run to terminalize, so awaiting it here would deadlock the thing it is
        // waiting for.
        hub.DecorateContext = inner => new ScriptedContextBuilder(inner)
        {
            BeforeBuild = () =>
            {
                operatorDecision =
                    service.CancelAsync("HUB-001", null, "no longer wanted", CancellationToken.None);

                Assert.True(
                    requested.Task.Wait(TimeSpan.FromSeconds(30)),
                    "the operator's request never reached the runner");
            },
        };

        var agentLaunched = false;
        hub.DecorateAgent = inner => new AnnouncingAgentGateway(inner, () => agentLaunched = true);

        IDisposable? listening = null;

        try
        {
            var report = await hub.Orchestrator().RunAsync(
                hub.TaskId,
                OrchestrationHarness.ScriptedProfileName,
                stopping.Token,
                executionId => listening = signal.Listen(
                    executionId,
                    () =>
                    {
                        stopping.Cancel();
                        requested.TrySetResult();
                    }));

            Assert.Equal(ExecutionState.Cancelled, report.State);
            Assert.Null(report.ResultCommitSha);
            Assert.False(agentLaunched);

            var decision = await operatorDecision!;

            // The operator got a real answer, not a request that timed out.
            Assert.Equal(TaskCancellationState.Cancelled, decision.State);
            Assert.Equal(report.ExecutionId, decision.ExecutionId);
            Assert.Equal(ExecutionState.Cancelled, decision.ExecutionState);
            Assert.Null(decision.ResultCommitSha);

            // Durably, and in the only honest order.
            var reopened = await hub.ReopenStoreAsync();
            var execution = (await reopened.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None)).Single();
            var task = await reopened.FindTaskAsync(hub.TaskId, CancellationToken.None);

            Assert.Equal(ExecutionState.Cancelled, execution.State);
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Cancelled, task!.State);
        }
        finally
        {
            listening?.Dispose();
        }
    }

    /// <summary>
    /// The shape every early cancellation has to end in: a terminal Cancelled execution
    /// with no commit, no step left running, no verification or review claimed, and a task
    /// handed back so the work can be attempted again.
    /// </summary>
    private static async Task AssertClosedAsync(
        OrchestrationHarness hub,
        ExecutionRunReport report,
        ExecutionStepKind? expectedRunningStep)
    {
        // Read durably. The close writes through a scope of its own, precisely so that a
        // save interrupted mid transition cannot decide what the record says - which means
        // the context the run used is not the place to ask what the record says either.
        var execution = await hub.DurableExecutionAsync(report.ExecutionId);

        Assert.True(execution.IsTerminal);
        Assert.Equal(ExecutionState.Cancelled, execution.State);
        Assert.Null(execution.Outcome);
        Assert.Null(execution.ResultCommitSha);

        var steps = await hub.DurableStepsAsync(report.ExecutionId);

        // Nothing is left mid flight: a step that was running is closed as cancelled.
        Assert.DoesNotContain(steps, s => s.State == ExecutionStepState.Running);

        if (expectedRunningStep is { } kind)
        {
            Assert.Equal(ExecutionStepState.Cancelled, steps.Single(s => s.Kind == kind).State);
        }

        // Nothing downstream may claim to have happened.
        foreach (var never in new[] { ExecutionStepKind.Verify, ExecutionStepKind.Review })
        {
            Assert.NotEqual(ExecutionStepState.Succeeded, steps.Single(s => s.Kind == never).State);
        }

        Assert.Equal(TaskState.Ready, (await hub.DurableTaskAsync()).State);
    }
}
