using CoreX.Hub.Application.Isolation;
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

        /// <summary>The runtime declared it cannot do this (ADR-0008). Never a success.</summary>
        Unsupported,
    }

    private sealed record ValidatedStart(
        HubTask Task,
        Guid RepositoryId,
        AgentProfile AgentProfile,
        int AttemptNo,
        Guid? PreviousExecutionId);

    private sealed record StartedExecution(
        Execution Execution,
        HubTask Task,
        Guid RepositoryId,
        AgentProfile AgentProfile);

    /// <summary>
    /// Validates what must be true before anything external happens, and decides which
    /// attempt this is.
    ///
    /// Nothing is written here. The execution cannot be created yet: it needs the commit it
    /// will be based on, and that answer belongs to the repository, which has not been
    /// asked at this point.
    /// </summary>
    private async Task<ValidatedStart> ValidateStartAsync(Guid taskId, string agentProfileName, CancellationToken ct)
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

        // Declared hard is intent; whether the machine provides it is a separate question,
        // asked here and nowhere else so the CLI and the API cannot answer it differently.
        //
        // The absence of an authority is not permission: a Hub with no isolation service wired
        // refuses a hard organization rather than treating the silence as a pass.
        if (organization.IsolationLevel == IsolationLevel.Hard)
        {
            var admission = _isolation is null
                ? IsolationAdmissionDecision.Refused(
                    IsolationRefusal.NotConfigured,
                    "this Hub has no hard isolation authority configured.")
                : await _isolation.EvaluateAsync(organization, ct);

            if (!admission.IsAllowed)
            {
                throw new ExecutionStartException(
                    $"Organization '{organization.Slug}' cannot start executions "
                    + $"[{admission.ReasonCode}]: {admission.Detail}");
            }
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

        var attempt = existing.Count == 0 ? 1 : existing.Max(e => e.AttemptNo) + 1;

        return new ValidatedStart(task, repository.Id, profile, attempt, existing.FirstOrDefault()?.Id);
    }

    /// <summary>
    /// Claims the execution in one transaction with its audit row, against the commit the
    /// repository already answered with.
    ///
    /// The base commit arrives as an argument rather than being filled in afterwards, and
    /// that is the point: from the first row written, this execution's provenance is a real
    /// resolved commit. There is no window in which it is a placeholder, so there is no
    /// crash that can leave one behind.
    /// </summary>
    private async Task<StartedExecution> PersistStartAsync(
        ValidatedStart validated,
        string baseCommitSha,
        CancellationToken ct)
    {
        var task = validated.Task;
        var now = _clock.GetUtcNow();

        // The worktree is created later, so its path is the one thing here still unknown.
        // The branch name is not: it follows from the attempt, and deciding it in one place
        // is what stops the workspace and the record from disagreeing about it.
        var branchName = $"agent/{task.TaskKey}/{validated.AttemptNo}";

        var execution = new Execution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            validated.AttemptNo,
            baseCommitSha,
            branchName,
            PendingWorktreePath,
            validated.PreviousExecutionId,
            now);

        execution.ClaimLease(_liveness.CurrentProcessId, _liveness.CurrentHostStartedAtUtc, now);
        task.MarkRunning(now);

        _store.AddExecution(execution);

        foreach (var (kind, order) in PipelineSteps)
        {
            _store.AddExecutionStep(new ExecutionStep(
                Guid.CreateVersion7(), task.OrganizationId, execution.Id, order, kind, now));
        }

        _store.AddAuditEvent(AuditFor(
            execution,
            "ExecutionStarted",
            $"attempt {validated.AttemptNo} of {task.TaskKey} on {baseCommitSha[..12]}"));

        await _store.SaveChangesAsync(ct);

        return new StartedExecution(execution, task, validated.RepositoryId, validated.AgentProfile);
    }

    /// <summary>
    /// Said in words rather than as a fake path, because it describes a directory that does
    /// not exist yet. Nothing measures it, nothing resolves it, and the workspace replaces
    /// it with the real path the moment there is one.
    /// </summary>
    private const string PendingWorktreePath = "(not created yet)";

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
