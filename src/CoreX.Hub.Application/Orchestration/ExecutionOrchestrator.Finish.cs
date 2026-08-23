using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// Ends with a verdict. Passed hands the task to a human; anything else hands it back
    /// to the queue. Accepted is never reachable from here - that is a person's decision.
    /// </summary>
    private async Task<ExecutionRunReport> CompleteAsync(
        Execution execution,
        HubTask task,
        ExecutionOutcome outcome,
        string detail,
        string? snapshotResidual,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        execution.Complete(outcome, now);

        if (outcome == ExecutionOutcome.Passed)
        {
            task.MarkAwaitingAcceptance(now);
        }
        else
        {
            task.ReturnToReady(Bounded(detail), now);
        }

        _store.AddOrchestrationEvent(EventFor(execution, "Completed", outcome + ": " + detail));
        _store.AddAuditEvent(AuditFor(execution, "ExecutionCompleted", outcome + ": " + Bounded(detail)));
        await _store.SaveChangesAsync(ct);

        return Report(execution, detail, snapshotResidual);
    }

    /// <summary>
    /// Ends without a verdict: the agent failed, ran out of time, was cancelled, or the
    /// host died. The task becomes available again - failure lives on the execution, and
    /// the worktree stays because it holds the only copy of whatever was done.
    /// </summary>
    private async Task<ExecutionRunReport> TerminateAsync(
        Execution execution,
        HubTask task,
        ExecutionState terminal,
        string detail,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        execution.Terminate(terminal, now);
        task.ReturnToReady(Bounded(detail), now);

        _store.AddOrchestrationEvent(EventFor(execution, terminal.ToString(), detail));
        _store.AddAuditEvent(AuditFor(execution, "ExecutionTerminated", terminal + ": " + Bounded(detail)));
        await _store.SaveChangesAsync(ct);

        return Report(execution, detail, null);
    }

    private static ExecutionRunReport Report(Execution execution, string detail, string? snapshotResidual) => new()
    {
        ExecutionId = execution.Id,
        State = execution.State,
        Outcome = execution.Outcome,
        ResultCommitSha = execution.ResultCommitSha,
        ContextPackHash = execution.ContextPackHash,
        UnexpectedAgentCommitDetected = execution.UnexpectedAgentCommitDetected,
        UnexpectedCommitShas = string.IsNullOrEmpty(execution.UnexpectedCommitShas)
            ? []
            : execution.UnexpectedCommitShas.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        Detail = detail,
        SnapshotResidual = snapshotResidual,
    };
}
