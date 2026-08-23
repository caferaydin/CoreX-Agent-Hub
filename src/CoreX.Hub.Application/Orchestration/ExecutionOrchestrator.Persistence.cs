using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    private const string HubActor = "hub:orchestrator";

    private enum StepFate
    {
        Succeeded,
        Failed,
        TimedOut,
        Cancelled,
    }

    private sealed record StartedExecution(
        Execution Execution,
        HubTask Task,
        Guid RepositoryId,
        AgentProfile AgentProfile);

    /// <summary>
    /// Validates what must be true before anything external happens, then claims the
    /// execution in one transaction with its audit row.
    /// </summary>
    private async Task<StartedExecution> StartAsync(Guid taskId, string agentProfileName, CancellationToken ct)
    {
        var task = await _store.FindTaskAsync(taskId, ct)
                   ?? throw new ExecutionStartException($"Task {taskId} does not exist.");

        if (task.State != TaskState.Ready)
        {
            throw new ExecutionStartException($"Task '{task.TaskKey}' is {task.State}, not Ready.");
        }

        var organization = await _store.FindOrganizationAsync(task.OrganizationId, ct)
                           ?? throw new ExecutionStartException("The task's organization no longer exists.");

        // ADR-0017: an organization that declares hard isolation it does not have is
        // blocked in code, not in a document.
        if (!organization.CanStartExecution)
        {
            throw new ExecutionStartException(
                $"Organization '{organization.Slug}' cannot start executions: {organization.BlockReason}");
        }

        var repository = await _store.FindRepositoryAsync(task.RepositoryId, ct)
                         ?? throw new ExecutionStartException("The task's repository no longer exists.");

        var profile = await _store.FindAgentProfileByNameAsync(task.OrganizationId, agentProfileName, ct)
                      ?? throw new ExecutionStartException($"Agent profile '{agentProfileName}' does not exist.");

        var existing = await _store.ListExecutionsForTaskAsync(task.Id, ct);

        if (existing.Any(e => !e.IsTerminal))
        {
            throw new ExecutionStartException($"Task '{task.TaskKey}' already has a non terminal execution.");
        }

        var now = _clock.GetUtcNow();
        var attempt = existing.Count == 0 ? 1 : existing.Max(e => e.AttemptNo) + 1;

        // The workspace is created later, but the execution has to exist first so the
        // attempt number and the branch name are decided in one place.
        var branchName = $"agent/{task.TaskKey}/{attempt}";

        var execution = new Execution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            attempt,
            PlaceholderBaseCommit,
            branchName,
            "(pending)",
            existing.FirstOrDefault()?.Id,
            now);

        execution.ClaimLease(_liveness.CurrentProcessId, _liveness.CurrentHostStartedAtUtc, now);
        task.MarkRunning(now);

        _store.AddExecution(execution);

        foreach (var (kind, order) in PipelineSteps)
        {
            _store.AddExecutionStep(new ExecutionStep(
                Guid.CreateVersion7(), task.OrganizationId, execution.Id, order, kind, now));
        }

        _store.AddAuditEvent(AuditFor(execution, "ExecutionStarted", $"attempt {attempt} of {task.TaskKey}"));
        await _store.SaveChangesAsync(ct);

        return new StartedExecution(execution, task, repository.Id, profile);
    }

    /// <summary>
    /// The base commit is not known until the workspace pins it, but the aggregate demands
    /// a valid object id at construction. This placeholder is replaced the moment the
    /// workspace answers, and never reaches a gate or a baseline key.
    /// </summary>
    private const string PlaceholderBaseCommit = "0000000000000000000000000000000000000000";

    private static readonly (ExecutionStepKind Kind, int Order)[] PipelineSteps =
    [
        (ExecutionStepKind.PrepareWorkspace, 1),
        (ExecutionStepKind.BuildContext, 2),
        (ExecutionStepKind.Agent, 3),
        (ExecutionStepKind.CaptureChanges, 4),
        (ExecutionStepKind.Verify, 5),
        (ExecutionStepKind.Review, 6),
    ];

    private static AuditEvent AuditFor(Execution execution, string eventType, string summary) =>
        new(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            eventType,
            HubActor,
            summary,
            execution.TaskId,
            execution.Id,
            execution.Id.ToString("D"),
            execution.UpdatedAtUtc);

    /// <summary>
    /// One transaction per transition: mutate, write the audit row, save. Nothing external
    /// happens inside it, so the database is never held open across a subprocess.
    /// </summary>
    private async Task PersistAsync(CancellationToken ct, Func<AuditEvent> mutate)
    {
        _store.AddAuditEvent(mutate());
        await _store.SaveChangesAsync(ct);
    }
}
