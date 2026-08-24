using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tasks;

/// <summary>What an operator's cancellation actually achieved.</summary>
public enum TaskCancellationState
{
    /// <summary>The work is stopped and the task is terminal.</summary>
    Cancelled,

    /// <summary>The runner was asked to stop and had not finished stopping in time.</summary>
    CancellationRequested,

    /// <summary>Nothing was asked and nothing was changed.</summary>
    Refused,
}

public sealed record TaskCancellationResult
{
    public required TaskCancellationState State { get; init; }

    public required string TaskKey { get; init; }

    public required TaskState TaskState { get; init; }

    public Guid? ExecutionId { get; init; }

    public ExecutionState? ExecutionState { get; init; }

    public string? ResultCommitSha { get; init; }

    public bool WorktreePreserved { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// The operator's "stop this and do not come back to it" decision.
///
/// It is deliberately not the same thing as an execution ending Cancelled. An execution is
/// cancelled when its caller stopped it, and the task then becomes available again so the
/// work can be attempted once more; that is orchestration's own business. This is a person
/// saying the task itself is finished with, and it is the only thing that writes
/// <see cref="Domain.Tasks.TaskState.Cancelled"/>.
///
/// The hard part is that the two can be true at once, and the Hub runs as separate command
/// line processes. When work really is in flight, flipping a column here would leave a
/// terminal task while an agent kept writing into a worktree - a record that contradicts
/// the machine. So a running task is cancelled in the only honest order: ask the process
/// that owns the execution to stop, wait for it to say in the database that it did, and
/// only then record the operator's decision. If it does not stop, nothing is written.
///
/// Execution truth is never rewritten to match. An attempt that got as far as a result
/// commit keeps it, and keeps whatever outcome it earned.
/// </summary>
public sealed class TaskCancellationService
{
    private const string HubActor = "hub:operator";

    /// <summary>The audit type an operator cancellation is recorded under.</summary>
    public const string AuditType = "TaskCancelledByOperator";

    private readonly Func<IHubStore> _open;
    private readonly IExecutionCancellationSignal? _signal;
    private readonly TimeProvider _clock;

    public TaskCancellationService(
        Func<IHubStore> open,
        IExecutionCancellationSignal? signal,
        TimeProvider clock)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _signal = signal;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// How long to wait for a signalled runner to finish stopping. Bounded on purpose: the
    /// runner still has to capture evidence after the agent dies, and the operator still
    /// has to get an answer.
    /// </summary>
    public TimeSpan StopDeadline { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the database is asked whether the run has stopped. Each read is a fresh
    /// connection - the runner is writing on its own - so this is deliberately not a tight
    /// loop.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public async Task<TaskCancellationResult> CancelAsync(
        string taskKey,
        string? organizationSlug,
        string? reason,
        CancellationToken ct)
    {
        var store = _open();
        var task = await FindTaskAsync(store, taskKey, organizationSlug, ct);

        if (TaskStateRules.IsTerminal(task.State))
        {
            return new TaskCancellationResult
            {
                State = TaskCancellationState.Refused,
                TaskKey = task.TaskKey.Value,
                TaskState = task.State,
                Detail = $"the task is already {task.State}, so there is nothing to cancel",
            };
        }

        var executions = await store.ListExecutionsForTaskAsync(task.Id, ct);
        var running = executions.FirstOrDefault(e => !e.IsTerminal);

        if (running is null)
        {
            // Nothing is in flight, so there is nothing to stop and no execution to
            // manufacture. Whatever earlier attempts left on disk stays exactly where it is.
            return await RecordAsync(store, task, null, reason, "no execution was running", ct);
        }

        return await StopAndCancelAsync(task, running, reason, ct);
    }

    /// <summary>
    /// Stops a live execution first, and records the operator's decision only afterwards.
    ///
    /// "Afterwards" is read from the database rather than assumed: the signal says a request
    /// was delivered, not that anything stopped. What proves the work is over is the runner
    /// writing a terminal execution - which it only does once the agent's process tree is
    /// gone and the mandatory evidence capture has been persisted.
    /// </summary>
    private async Task<TaskCancellationResult> StopAndCancelAsync(
        HubTask task,
        Execution running,
        string? reason,
        CancellationToken ct)
    {
        if (_signal is null)
        {
            return Pending(
                TaskCancellationState.Refused, task, running,
                "cancelling running work is not available on this platform, so nothing was signalled");
        }

        if (!_signal.TryRequest(running.Id))
        {
            // Either the runner already exited or it died. Either way nobody is listening,
            // so nothing can be stopped from here - and a task that says Cancelled while a
            // process may still be writing would be a worse answer than none. Restart
            // reconciliation owns an abandoned execution.
            return Pending(
                TaskCancellationState.Refused, task, running,
                $"no running process is listening for execution {running.Id:D}; if its host died, the next "
                + "hub command reconciles it");
        }

        var deadline = _clock.GetUtcNow() + StopDeadline;

        while (true)
        {
            var polling = _open();
            var observed = await polling.FindExecutionAsync(running.Id, ct);

            if (observed is null)
            {
                return Pending(
                    TaskCancellationState.Refused, task, running,
                    "the execution disappeared while it was being stopped");
            }

            if (observed.IsTerminal)
            {
                return await ConfirmStoppedAsync(polling, task, observed, reason, ct);
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                // Asked, not achieved. Saying "cancelled" here would be a claim about a
                // process that is demonstrably still running.
                return Pending(
                    TaskCancellationState.CancellationRequested, task, observed,
                    $"execution {observed.Id:D} was asked to stop and was still {observed.State} after "
                    + $"{StopDeadline.TotalSeconds:0} seconds; the task was left as it is");
            }

            await Task.Delay(PollInterval, _clock, ct);
        }
    }

    /// <summary>
    /// Confirms the run really is over, then records the decision.
    ///
    /// A terminal execution is necessary but not sufficient when an agent ran: the mandatory
    /// capture is what turns a killed agent into a record of what it did, and a task
    /// cancelled before that finished would be terminal over evidence nobody wrote. The
    /// step's own state is the durable answer to whether it happened.
    ///
    /// "Not captured" and "nothing to capture" are different things, though, and only the
    /// first is a reason to refuse. A run stopped before the agent ever started wrote no
    /// agent output, so its capture step is Pending because the pipeline never reached it -
    /// and it never will, because the execution is already terminal. Waiting for it would
    /// mean waiting out the deadline for something that has already finished, which is what
    /// made an operator unable to cancel a task during PreparingWorkspace or BuildingContext
    /// at all.
    /// </summary>
    private async Task<TaskCancellationResult> ConfirmStoppedAsync(
        IHubStore store,
        HubTask task,
        Execution stopped,
        string? reason,
        CancellationToken ct)
    {
        var steps = await store.ListExecutionStepsAsync(stopped.Id, ct);
        var capture = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.CaptureChanges);
        var agent = steps.FirstOrDefault(s => s.Kind == ExecutionStepKind.Agent);

        // Whether an agent ever started is what decides if a missing capture matters. The
        // agent step leaves Pending the moment the run reaches it, so Pending here means no
        // agent process existed and there is no output anyone failed to record.
        var agentRan = agent is not null && agent.State != ExecutionStepState.Pending;

        if (agentRan && capture is { State: ExecutionStepState.Pending or ExecutionStepState.Running })
        {
            return Pending(
                TaskCancellationState.CancellationRequested, task, stopped,
                $"execution {stopped.Id:D} is {stopped.State} but its change capture is still "
                + $"{capture.State}; the task was left as it is");
        }

        // Re-read through this store: the runner has written to the task since it was first
        // loaded - a stopped attempt hands it back to Ready - and cancelling a stale copy
        // would either fail or overwrite what the runner recorded.
        var current = await store.FindTaskAsync(task.Id, ct)
                      ?? throw new TaskOperationException($"Task '{task.TaskKey}' disappeared while it was stopping.");

        if (TaskStateRules.IsTerminal(current.State))
        {
            return Pending(
                TaskCancellationState.Refused, current, stopped,
                $"the task became {current.State} while the execution was stopping");
        }

        return await RecordAsync(store, current, stopped, reason, $"execution {stopped.State}", ct);
    }

    /// <summary>
    /// Writes the operator's decision and nothing else.
    ///
    /// The execution is passed only so the audit row can name it. Nothing about it is
    /// changed here: an attempt that produced a result commit keeps the commit and keeps
    /// its own outcome, because the task being abandoned is not a claim about what the
    /// machine measured.
    /// </summary>
    private async Task<TaskCancellationResult> RecordAsync(
        IHubStore store,
        HubTask task,
        Execution? execution,
        string? reason,
        string context,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        task.CancelByOperator(reason, now);

        store.AddAuditEvent(new AuditEvent(
            Guid.CreateVersion7(),
            task.OrganizationId,
            AuditType,
            HubActor,
            reason is { Length: > 0 } ? $"{context}: {reason}" : context,
            task.Id,
            execution?.Id,
            task.TaskKey.Value,
            now));

        await store.SaveChangesAsync(ct);

        return new TaskCancellationResult
        {
            State = TaskCancellationState.Cancelled,
            TaskKey = task.TaskKey.Value,
            TaskState = task.State,
            ExecutionId = execution?.Id,
            ExecutionState = execution?.State,
            ResultCommitSha = execution?.ResultCommitSha,
            WorktreePreserved = execution?.WorktreeMustBePreserved ?? false,
            Detail = context,
        };
    }

    private static TaskCancellationResult Pending(
        TaskCancellationState state,
        HubTask task,
        Execution execution,
        string detail) =>
        new()
        {
            State = state,
            TaskKey = task.TaskKey.Value,
            TaskState = task.State,
            ExecutionId = execution.Id,
            ExecutionState = execution.State,
            ResultCommitSha = execution.ResultCommitSha,
            WorktreePreserved = execution.WorktreeMustBePreserved,
            Detail = detail,
        };

    private static async Task<HubTask> FindTaskAsync(
        IHubStore store,
        string taskKey,
        string? organizationSlug,
        CancellationToken ct)
    {
        if (!TaskKey.TryParse(taskKey, out var parsed))
        {
            throw new TaskOperationException(
                $"'{taskKey}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.");
        }

        var matches = await store.FindTasksByKeyAsync(parsed.Value, ct);

        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await store.FindOrganizationBySlugAsync(organizationSlug, ct)
                ?? throw new TaskOperationException($"Organization '{organizationSlug}' was not found.");

            matches = [.. matches.Where(t => t.OrganizationId == organization.Id)];
        }

        return matches.Count switch
        {
            0 => throw new TaskOperationException($"Task '{parsed}' was not found."),
            1 => matches[0],
            _ => throw new TaskOperationException(
                $"Task '{parsed}' exists in more than one organization; pass --org to say which."),
        };
    }
}
