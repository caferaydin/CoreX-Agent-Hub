using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Orchestration;

public sealed record ReconciledExecution(Guid ExecutionId, Guid TaskId, bool WorktreePreserved, string Reason);

public sealed record StaleExecutionReport(IReadOnlyList<ReconciledExecution> Interrupted)
{
    public string Describe() =>
        Interrupted.Count == 0
            ? "no stale executions"
            : $"{Interrupted.Count} execution(s) marked Interrupted";
}

/// <summary>
/// Finds executions nobody is driving any more and closes them honestly.
///
/// A crash leaves a row that says Running for ever. This marks those Interrupted and
/// hands the task back, so the work can be attempted again - and it never touches disk:
/// the worktree of an execution without a result commit holds the only copy of whatever
/// the agent did, and the workspace module owns disk reconciliation anyway.
///
/// Ownership is the pair of process id and host start time, because operating systems
/// reuse process ids and a recycled one is not the original owner.
/// </summary>
public sealed class StaleExecutionReconciler
{
    private const string HubActor = "hub:reconciler";

    private readonly IHubStore _store;
    private readonly IProcessLivenessProbe _liveness;
    private readonly TimeProvider _clock;
    private readonly ExecutionOrchestratorOptions _options;

    public StaleExecutionReconciler(
        IHubStore store,
        IProcessLivenessProbe liveness,
        TimeProvider clock,
        ExecutionOrchestratorOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<StaleExecutionReport> ReconcileAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var interrupted = new List<ReconciledExecution>();

        foreach (var execution in await _store.ListNonTerminalExecutionsAsync(ct))
        {
            var ownerIsAlive = execution.LeaseProcessId is { } pid
                               && execution.LeaseHostStartedAtUtc is { } started
                               && _liveness.IsAlive(pid, started);

            if (!execution.IsLeaseStale(ownerIsAlive, now, _options.MaximumLeaseSilence))
            {
                continue;
            }

            var reason = ownerIsAlive
                ? "the owner stopped reporting"
                : "the owning process is gone";

            var task = await _store.FindTaskAsync(execution.TaskId, ct);

            execution.Terminate(ExecutionState.Interrupted, now);
            task?.ReturnToReady("a previous execution was interrupted", now);

            _store.AddAuditEvent(new AuditEvent(
                Guid.CreateVersion7(),
                execution.OrganizationId,
                "ExecutionInterrupted",
                HubActor,
                reason,
                execution.TaskId,
                execution.Id,
                execution.Id.ToString("D"),
                now));

            await _store.SaveChangesAsync(ct);

            interrupted.Add(new ReconciledExecution(
                execution.Id, execution.TaskId, execution.WorktreeMustBePreserved, reason));
        }

        return new StaleExecutionReport(interrupted);
    }
}
