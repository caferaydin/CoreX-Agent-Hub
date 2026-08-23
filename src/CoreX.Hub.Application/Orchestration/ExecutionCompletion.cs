using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

/// <summary>
/// What "this execution is finished" means for the pair of rows, in one place.
///
/// Two callers reach it: the pipeline that ran the work, and the recovery path that picks
/// up an execution whose host died after its result commit existed. They have to agree
/// exactly - a Passed run must hand the task to a person from either direction, and neither
/// may reach Accepted - so the rule is written once rather than twice.
/// </summary>
internal static class ExecutionCompletion
{
    /// <summary>
    /// Ends the execution with a verdict and moves the task accordingly.
    ///
    /// Passed hands the task to a human and nothing else; anything else hands it back to
    /// the queue, because failure lives on the execution and there is no failed task. A
    /// task an operator has already abandoned is left alone: the execution still records
    /// what the machine found, and the terminal task is a separate decision that outranks
    /// requeueing it.
    /// </summary>
    public static void Apply(
        Execution execution,
        HubTask task,
        ExecutionOutcome outcome,
        string detail,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(task);

        execution.Complete(outcome, nowUtc);

        if (TaskStateRules.IsTerminal(task.State))
        {
            return;
        }

        if (outcome == ExecutionOutcome.Passed)
        {
            task.MarkAwaitingAcceptance(nowUtc);
        }
        else
        {
            task.ReturnToReady(Bounded(detail), nowUtc);
        }
    }

    /// <summary>The timeline carries summaries. Content belongs to the evidence store.</summary>
    public static string Bounded(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? "(none)"
            : message.Length <= 1000 ? message : message[..1000];
}
