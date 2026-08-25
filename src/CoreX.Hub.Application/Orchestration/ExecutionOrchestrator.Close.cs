using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// Ends a run the caller cancelled, from what the database says rather than from what
    /// the interrupted write intended.
    ///
    /// The budget starts here rather than earlier because the caller has already stopped:
    /// this is precisely the work a cancelled run still owes, and the same bounded allowance
    /// every other mandatory section gets applies to it. A stuck write cannot hold a
    /// cancelled run open for ever, and a cancelled caller cannot stop the record.
    ///
    /// It takes identifiers rather than the aggregates the pipeline was holding, and that is
    /// the whole of the fix. Cancellation can land inside a save, between the mutation and
    /// the commit, and what it leaves behind is a tracked instance that already says
    /// terminal over a row that does not. Reasoning from that instance - whether by
    /// transitioning it again or by checking it and returning early - reads an intention
    /// that was abandoned. So nothing tracked crosses into here; the durable rows are read
    /// again, in a scope of their own, and they are the authority.
    ///
    /// Cancelled is not Failed. The distinction survives all the way to the row, because
    /// "somebody stopped this" and "this went wrong" are different facts about the attempt
    /// and only one of them is about the work.
    /// </summary>
    private async Task<ExecutionRunReport> CloseCancelledAsync(Guid executionId, Guid taskId)
    {
        using var closing = new MandatoryWorkScope(_options.MandatoryWorkBudget, _clock);
        var record = closing.Token;

        // A scope of its own, so neither the read nor the write can be contaminated by the
        // mutations of the save that was interrupted.
        var store = _detached.Open();

        var execution = await store.FindExecutionAsync(executionId, record);
        var task = await store.FindTaskAsync(taskId, record);

        if (execution is null || task is null)
        {
            // Nothing is fabricated to fill the gap. An execution whose durable truth cannot
            // be read is exactly what the stale reconciler is the last resort for.
            throw new ExecutionFinalizationException(
                $"Execution {executionId:D} could not be closed: its durable state could not be read.");
        }

        await CancelUnfinishedStepAsync(store, executionId, record);

        // Case B - the interrupted save had already reached the database, or something else
        // closed this execution first. Either way machine truth exists and stands: it is not
        // transitioned again and its outcome is not rewritten to match the caller's
        // intention, because what actually happened outranks what was being recorded when
        // the caller stopped.
        if (execution.IsTerminal)
        {
            return await AcknowledgeClosedAsync(store, execution, task, record);
        }

        // Case A with a commit - the attempt produced something. Every terminal error state
        // asserts that it produced nothing, so none of them can describe it, and the commit
        // is never disowned to make a tidier story.
        if (execution.ResultCommitSha is not null)
        {
            var outcome = await CommitBearingOutcomeAsync(store, executionId, record);

            return await CloseWithOutcomeAsync(
                store,
                execution,
                task,
                outcome,
                outcome == ExecutionOutcome.ReviewUnusable
                    ? "the run was cancelled after its result commit was verified, so no review exists"
                    : "the run was cancelled after its result commit was created",
                record);
        }

        // Case A without a commit - the ordinary cancellation, and the only shape that may
        // be called Cancelled.
        return await CloseTerminatedAsync(
            store,
            execution,
            task,
            ExecutionState.Cancelled,
            "the run was cancelled before a result commit was created",
            record);
    }

    /// <summary>
    /// What a cancelled attempt that already committed is called.
    ///
    /// Read from the durable steps rather than assumed, because the two answers mean
    /// different things and the accepted mappings for them are different. A run stopped
    /// while the machine was still measuring did not produce a verified result, so it is
    /// VerificationFailed; a run stopped after that measurement succeeded has a verified
    /// result nobody reviewed, which is exactly what ReviewUnusable says. Calling the second
    /// one VerificationFailed would contradict a Verify step recorded as Succeeded.
    /// </summary>
    private static async Task<ExecutionOutcome> CommitBearingOutcomeAsync(
        IHubStore store,
        Guid executionId,
        CancellationToken record)
    {
        var steps = await store.ListExecutionStepsAsync(executionId, record);
        var verify = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.Verify);

        return verify is { State: ExecutionStepState.Succeeded }
            ? ExecutionOutcome.ReviewUnusable
            : ExecutionOutcome.VerificationFailed;
    }

    /// <summary>
    /// Records that the close found the execution already ended, and moves nothing that has
    /// already moved.
    ///
    /// The task is looked at because the pair has to be consistent, not because it is
    /// expected to be wrong: an execution and its task move in one save, so a task still
    /// Running under a terminal execution would mean a write that half landed. If it ever
    /// happens it is repaired here rather than left for a person to find.
    /// </summary>
    private async Task<ExecutionRunReport> AcknowledgeClosedAsync(
        IHubStore store,
        Execution execution,
        HubTask task,
        CancellationToken record)
    {
        var detail = $"the run was cancelled; the execution had already ended as {execution.State}"
                     + (execution.Outcome is { } outcome ? $" ({outcome})" : string.Empty);

        var now = _clock.GetUtcNow();

        if (task.State == TaskState.Running)
        {
            task.ReturnToReady(Bounded(detail), now);
        }

        store.AddOrchestrationEvent(EventFor(execution, "CancelledAfterClose", detail));
        store.AddAuditEvent(AuditFor(execution, "ExecutionAlreadyClosed", Bounded(detail)));

        await store.SaveChangesAsync(record);

        return Report(execution, detail, null);
    }

    /// <summary>Ends a durably non terminal execution without a verdict, once.</summary>
    private async Task<ExecutionRunReport> CloseTerminatedAsync(
        IHubStore store,
        Execution execution,
        HubTask task,
        ExecutionState terminal,
        string detail,
        CancellationToken record)
    {
        var now = _clock.GetUtcNow();

        execution.Terminate(terminal, now);

        // Back to the queue, and only from Running: an operator who has already abandoned
        // the task owns that decision, and this must not undo it.
        if (task.State == TaskState.Running)
        {
            task.ReturnToReady(Bounded(detail), now);
        }

        store.AddOrchestrationEvent(EventFor(execution, terminal.ToString(), detail));
        store.AddAuditEvent(AuditFor(execution, "ExecutionTerminated", terminal + ": " + Bounded(detail)));

        await store.SaveChangesAsync(record);

        return Report(execution, detail, null);
    }

    /// <summary>Ends a durably non terminal execution that already produced a commit.</summary>
    private async Task<ExecutionRunReport> CloseWithOutcomeAsync(
        IHubStore store,
        Execution execution,
        HubTask task,
        ExecutionOutcome outcome,
        string detail,
        CancellationToken record)
    {
        var now = _clock.GetUtcNow();

        ExecutionCompletion.Apply(execution, task, outcome, detail, now);

        store.AddOrchestrationEvent(EventFor(execution, "Completed", outcome + ": " + detail));
        store.AddAuditEvent(AuditFor(execution, "ExecutionCompleted", outcome + ": " + Bounded(detail)));

        await store.SaveChangesAsync(record);

        return Report(execution, detail, null);
    }

    /// <summary>
    /// Closes whichever step was still running, so nothing is left mid flight.
    ///
    /// Only one can be: the pipeline starts a step and finishes it before starting the next.
    /// Steps that never began stay Pending, which is honest - they did not run.
    ///
    /// It takes the store it should write through rather than using the orchestrator's,
    /// because the step rows are read and written in the same scope as everything else this
    /// close does.
    /// </summary>
    private async Task CancelUnfinishedStepAsync(IHubStore store, Guid executionId, CancellationToken record)
    {
        var steps = await store.ListExecutionStepsAsync(executionId, record);
        var running = steps.FirstOrDefault(s => s.State == ExecutionStepState.Running);

        if (running is null)
        {
            return;
        }

        running.Cancel("the run was cancelled", _clock.GetUtcNow());
        await store.SaveChangesAsync(record);
    }
}

/// <summary>
/// The durable truth a cancelled run owes could not be established.
///
/// Raised rather than papered over: a terminal row invented from an unreadable state would
/// be a claim about an attempt nobody can account for. The stale reconciler remains the last
/// resort for exactly this, and it is a last resort precisely because it is not supposed to
/// be reached by an ordinary cancellation.
/// </summary>
public sealed class ExecutionFinalizationException : Exception
{
    public ExecutionFinalizationException(string message)
        : base(message)
    {
    }

    public ExecutionFinalizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ExecutionFinalizationException()
    {
    }
}
