using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Cancellation that lands inside a write after the result commit already exists.
///
/// This is the half of the close path that must never say Cancelled. A commit is real in the
/// repository whatever the row says, and every terminal error state asserts the attempt
/// produced nothing - so the commit is kept and the run is completed with the outcome that
/// matches how far it actually got.
///
/// How far it got is read from the durable steps, not assumed. A run stopped while the
/// machine was still measuring did not produce a verified result; a run stopped after that
/// measurement succeeded produced one nobody reviewed. Calling the second VerificationFailed
/// would contradict a Verify step recorded as Succeeded.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DurableCancellationAfterCommitTests
{
    [Fact]
    public async Task A_cancellation_while_the_machine_was_still_measuring_keeps_the_commit_and_fails_verification()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        InterruptedStore? store = null;
        hub.DecorateStore = inner => store = new InterruptedStore(inner) { OnInterrupt = cancellation.Cancel };

        // Armed the moment the measurement is done: the very next write is the one that
        // records how it went, and it never lands.
        hub.DecorateVerifier = inner => new ScriptedVerifier(inner)
        {
            AfterVerify = () => store!.InterruptAfter(0),
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(store!.Interrupted);

        var durable = await Durable(hub, report.ExecutionId);

        Assert.Equal(ExecutionState.Completed, durable.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, durable.Outcome);
        Assert.NotNull(durable.ResultCommitSha);

        // Never Cancelled: that state asserts the attempt produced nothing, and it produced
        // a commit that is really in the repository.
        Assert.NotEqual(ExecutionState.Cancelled, durable.State);

        // Read durably, not from the context whose save was thrown away: that one still
        // holds whatever the abandoned transition had already mutated.
        Assert.Equal(TaskState.Ready, await DurableTaskStateAsync(hub));
    }

    /// <summary>
    /// The Phase 7 half: verification succeeded, the review was interrupted, and the
    /// execution is completed as the review that does not exist - not as a verification that
    /// did.
    /// </summary>
    [Fact]
    public async Task A_cancellation_during_the_review_keeps_the_commit_and_reports_no_usable_review()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        InterruptedStore? store = null;
        hub.DecorateStore = inner => store = new InterruptedStore(inner) { OnInterrupt = cancellation.Cancel };

        var reviewer = ScriptedReviewer.Clean();

        // The reviewer answers, and the first write that would record its answer is thrown
        // away - which is the window the generic close path has to describe correctly.
        reviewer.OnReview = () => store!.InterruptAfter(0);
        hub.Reviewer = reviewer;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.True(store!.Interrupted);
        Assert.Equal(1, reviewer.Calls);

        var durable = await Durable(hub, report.ExecutionId);

        Assert.Equal(ExecutionState.Completed, durable.State);
        Assert.Equal(ExecutionOutcome.ReviewUnusable, durable.Outcome);
        Assert.NotNull(durable.ResultCommitSha);
        Assert.NotEqual(ExecutionState.Cancelled, durable.State);

        // The verification really did succeed, and the record still says so.
        var reopened = await hub.ReopenStoreAsync();
        var steps = await reopened.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

        Assert.Equal(
            ExecutionStepState.Succeeded,
            steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);

        // And a review nobody could obtain is not a review that passed.
        Assert.False(AcceptanceRules.IsPlainAcceptanceEligible(durable.Outcome!.Value));
        Assert.False(AcceptanceRules.IsReviewOverrideEligible(durable.Outcome.Value));
    }

    /// <summary>
    /// The ordinary Phase 7 cancellation, unchanged by any of this: the reviewer itself was
    /// stopped, and the review step says so.
    /// </summary>
    [Fact]
    public async Task A_reviewer_that_was_cancelled_still_ends_as_a_completed_execution_with_its_commit()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;
        hub.Reviewer = ScriptedReviewer.Cancelled();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.ReviewUnusable, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

        Assert.Equal(
            ExecutionStepState.Cancelled,
            steps.Single(s => s.Kind == ExecutionStepKind.Review).State);

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    /// <summary>
    /// A commit bearing run closed this way is finished, and a new process must not find
    /// anything to repair.
    /// </summary>
    [Fact]
    public async Task A_commit_bearing_cancellation_needs_no_recovery_in_a_new_process()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        InterruptedStore? store = null;
        hub.DecorateStore = inner => store = new InterruptedStore(inner) { OnInterrupt = cancellation.Cancel };

        hub.DecorateVerifier = inner => new ScriptedVerifier(inner)
        {
            AfterVerify = () => store!.InterruptAfter(0),
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        var reopened = await hub.ReopenStoreAsync();

        var execution = await reopened.FindExecutionAsync(report.ExecutionId, CancellationToken.None);

        Assert.True(execution!.IsTerminal);

        var reconciled = await new StaleExecutionReconciler(
            reopened, hub.Liveness, hub.Clock, new ExecutionOrchestratorOptions())
            .ReconcileAsync(CancellationToken.None);

        Assert.Empty(reconciled.Interrupted);
        Assert.Empty(reconciled.RecoveryRequired);
    }

    private static async Task<TaskState> DurableTaskStateAsync(OrchestrationHarness hub)
    {
        var store = hub.OpenStore();

        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);

        return task!.State;
    }

    private static async Task<DomainExecution> Durable(OrchestrationHarness hub, Guid executionId)
    {
        var store = hub.OpenStore();

        return await store.FindExecutionAsync(executionId, CancellationToken.None)
               ?? throw new InvalidOperationException("the execution row disappeared");
    }
}
