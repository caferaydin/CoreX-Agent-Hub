using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Orchestration;

public sealed record ReconciledExecution(Guid ExecutionId, Guid TaskId, bool WorktreePreserved, string Reason);

/// <summary>
/// A stale execution that already created its result commit, reported rather than closed.
///
/// It is a reconciliation result, not a state: nothing about it is persisted, because there
/// is no honest terminal state to persist. Every terminal error state asserts that the
/// attempt produced nothing, and this one produced a commit that exists in the repository.
/// Writing Interrupted over it would make the row contradict the repository, and the only
/// ways to make Interrupted true - dropping the commit, resetting, rewriting - are exactly
/// the things the Hub never does.
///
/// So the execution stays where it is, the commit stays where it is, and closing this out
/// belongs to restart recovery.
/// </summary>
public sealed record PostCommitRecoveryRequired(
    Guid ExecutionId,
    Guid TaskId,
    string ResultCommitSha,
    ExecutionState State,
    string Reason);

public sealed record StaleExecutionReport(
    IReadOnlyList<ReconciledExecution> Interrupted,
    IReadOnlyList<PostCommitRecoveryRequired> RecoveryRequired)
{
    public string Describe() => (Interrupted.Count, RecoveryRequired.Count) switch
    {
        (0, 0) => "no stale executions",
        (_, 0) => $"{Interrupted.Count} execution(s) marked Interrupted",
        (0, _) => $"{RecoveryRequired.Count} execution(s) need recovery after their result commit",
        _ => $"{Interrupted.Count} execution(s) marked Interrupted; "
             + $"{RecoveryRequired.Count} need recovery after their result commit",
    };
}

/// <summary>
/// Finds executions nobody is driving any more and closes them honestly.
///
/// A crash leaves a row that says Running for ever. This marks those Interrupted and
/// hands the task back, so the work can be attempted again - and it never touches disk:
/// the worktree of an execution without a result commit holds the only copy of whatever
/// the agent did, and the workspace module owns disk reconciliation anyway.
///
/// One case is deliberately not closed: a crash after the result commit was created. That
/// attempt produced something, so no terminal error state describes it, and it is reported
/// for recovery instead of being forced into one.
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
        var recoveryRequired = new List<PostCommitRecoveryRequired>();

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

            // The crash window that cannot be closed by writing a state: the result commit
            // was created and the host died before the pipeline could finish with it. There
            // is no terminal error state that describes an attempt which produced a commit,
            // and the alternatives - dropping the commit from the record, resetting the
            // ref - would make the Hub lie about a repository it does not own. So this one
            // is reported and left exactly as it is: no state written, no task moved, no
            // worktree touched.
            if (execution.ResultCommitSha is { } resultCommit)
            {
                recoveryRequired.Add(new PostCommitRecoveryRequired(
                    execution.Id, execution.TaskId, resultCommit, execution.State, reason));

                continue;
            }

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

        return new StaleExecutionReport(interrupted, recoveryRequired);
    }
}
