using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A store whose save can be interrupted exactly once, deterministically.
///
/// It exists to produce the one state the production code cannot otherwise be shown in: a
/// tracked aggregate that has already been mutated into a terminal state while the row in
/// the database is still non terminal. That divergence is real - it is what an EF
/// SaveChanges cancelled mid flight leaves behind - and it is invisible from inside the
/// context that caused it, because the identity map hands back the mutated instance.
///
/// Two interruptions are offered because they mean different things, and the finalizer has
/// to survive both: a save that never reached the database, and a save that reached it and
/// was cancelled on the way back.
/// </summary>
public sealed class InterruptedStore : IHubStore
{
    private readonly IHubStore _inner;

    private int _armed = -1;
    private int _saves;

    public InterruptedStore(IHubStore inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>What to do before throwing - typically cancelling the caller's token.</summary>
    public Action? OnInterrupt { get; set; }

    /// <summary>Whether the interrupted save is allowed to reach the database first.</summary>
    public bool CommitBeforeThrowing { get; set; }

    public bool Interrupted { get; private set; }

    /// <summary>
    /// Interrupts the save <paramref name="skip"/> saves from now.
    ///
    /// Counted rather than matched on content because a store cannot see which aggregate a
    /// save carries; every test that arms this asserts afterwards which transition was
    /// actually caught, so a miscount fails loudly instead of passing vacuously.
    /// </summary>
    public void InterruptAfter(int skip)
    {
        _armed = _saves + skip;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        var ordinal = _saves++;

        if (ordinal != _armed)
        {
            return await _inner.SaveChangesAsync(ct);
        }

        _armed = -1;
        Interrupted = true;

        var written = 0;

        if (CommitBeforeThrowing)
        {
            // The write really happened; only the answer was lost. Nothing about the
            // exception tells the caller which of the two this was.
            written = await _inner.SaveChangesAsync(CancellationToken.None);
        }

        OnInterrupt?.Invoke();

        throw new OperationCanceledException(
            $"the save carrying transition {ordinal} was interrupted (committed: {CommitBeforeThrowing})");
    }

    public Task<IReadOnlyList<Organization>> ListOrganizationsAsync(CancellationToken ct) =>
        _inner.ListOrganizationsAsync(ct);

    public Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken ct) =>
        _inner.FindOrganizationAsync(id, ct);

    public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
        _inner.FindOrganizationBySlugAsync(slug, ct);

    public Task<IReadOnlyList<Project>> ListProjectsAsync(Guid? organizationId, CancellationToken ct) =>
        _inner.ListProjectsAsync(organizationId, ct);

    public Task<Project?> FindProjectAsync(Guid id, CancellationToken ct) => _inner.FindProjectAsync(id, ct);

    public Task<Project?> FindProjectBySlugAsync(Guid organizationId, string slug, CancellationToken ct) =>
        _inner.FindProjectBySlugAsync(organizationId, slug, ct);

    public Task<IReadOnlyList<Repository>> ListRepositoriesAsync(
        Guid? organizationId,
        Guid? projectId,
        CancellationToken ct) => _inner.ListRepositoriesAsync(organizationId, projectId, ct);

    public Task<Repository?> FindRepositoryAsync(Guid id, CancellationToken ct) =>
        _inner.FindRepositoryAsync(id, ct);

    public Task<Repository?> FindRepositoryBySlugAsync(Guid projectId, string slug, CancellationToken ct) =>
        _inner.FindRepositoryBySlugAsync(projectId, slug, ct);

    public Task<IReadOnlyList<VerificationProfile>> ListVerificationProfilesAsync(
        Guid repositoryId,
        CancellationToken ct) => _inner.ListVerificationProfilesAsync(repositoryId, ct);

    public Task<VerificationProfile?> FindDefaultVerificationProfileAsync(
        Guid repositoryId,
        CancellationToken ct) => _inner.FindDefaultVerificationProfileAsync(repositoryId, ct);

    public Task<IReadOnlyList<AgentProfile>> ListAgentProfilesAsync(Guid organizationId, CancellationToken ct) =>
        _inner.ListAgentProfilesAsync(organizationId, ct);

    public Task<AgentProfile?> FindAgentProfileByNameAsync(Guid organizationId, string name, CancellationToken ct) =>
        _inner.FindAgentProfileByNameAsync(organizationId, name, ct);

    public Task<IReadOnlyList<HubTask>> FindTasksByKeyAsync(string taskKey, CancellationToken ct) =>
        _inner.FindTasksByKeyAsync(taskKey, ct);

    public Task<HubTask?> FindTaskAsync(Guid id, CancellationToken ct) => _inner.FindTaskAsync(id, ct);

    public Task<IReadOnlyList<HubTask>> ListTasksAsync(
        Guid? organizationId,
        Guid? projectId,
        CancellationToken ct) => _inner.ListTasksAsync(organizationId, projectId, ct);

    public void AddOrganization(Organization organization) => _inner.AddOrganization(organization);

    public void AddProject(Project project) => _inner.AddProject(project);

    public void AddRepository(Repository repository) => _inner.AddRepository(repository);

    public void AddVerificationProfile(VerificationProfile profile) => _inner.AddVerificationProfile(profile);

    public void AddAgentProfile(AgentProfile profile) => _inner.AddAgentProfile(profile);

    public void AddTask(HubTask task) => _inner.AddTask(task);

    public Task<DomainExecution?> FindExecutionAsync(Guid id, CancellationToken ct) => _inner.FindExecutionAsync(id, ct);

    public Task<IReadOnlyList<DomainExecution>> ListExecutionsForTaskAsync(Guid taskId, CancellationToken ct) =>
        _inner.ListExecutionsForTaskAsync(taskId, ct);

    public Task<IReadOnlyList<DomainExecution>> ListNonTerminalExecutionsAsync(CancellationToken ct) =>
        _inner.ListNonTerminalExecutionsAsync(ct);

    public Task<IReadOnlyList<ExecutionStep>> ListExecutionStepsAsync(Guid executionId, CancellationToken ct) =>
        _inner.ListExecutionStepsAsync(executionId, ct);

    public Task<IReadOnlyList<CommandResult>> ListVerificationCommandsAsync(
        Guid executionId,
        CancellationToken ct) => _inner.ListVerificationCommandsAsync(executionId, ct);

    public Task<IReadOnlyList<Finding>> ListFindingsAsync(Guid executionId, CancellationToken ct) =>
        _inner.ListFindingsAsync(executionId, ct);

    public Task<IReadOnlyList<EvidenceArtifact>> ListEvidenceAsync(Guid executionId, CancellationToken ct) =>
        _inner.ListEvidenceAsync(executionId, ct);

    public Task<IReadOnlyList<OrchestrationEvent>> ListOrchestrationEventsAsync(
        Guid executionId,
        CancellationToken ct) => _inner.ListOrchestrationEventsAsync(executionId, ct);

    public Task<int> CountAcceptanceDecisionsAsync(Guid executionId, CancellationToken ct) =>
        _inner.CountAcceptanceDecisionsAsync(executionId, ct);

    public Task<AcceptanceDecision?> FindAcceptanceDecisionAsync(Guid executionId, CancellationToken ct) =>
        _inner.FindAcceptanceDecisionAsync(executionId, ct);

    public void AddExecution(DomainExecution execution) => _inner.AddExecution(execution);

    public void AddExecutionStep(ExecutionStep step) => _inner.AddExecutionStep(step);

    public void AddCommandResult(CommandResult commandResult) => _inner.AddCommandResult(commandResult);

    public void AddFinding(Finding finding) => _inner.AddFinding(finding);

    public void AddAcceptanceDecision(AcceptanceDecision decision) => _inner.AddAcceptanceDecision(decision);

    public void AddEvidenceArtifact(EvidenceArtifact artifact) => _inner.AddEvidenceArtifact(artifact);

    public void AddOrchestrationEvent(OrchestrationEvent orchestrationEvent) =>
        _inner.AddOrchestrationEvent(orchestrationEvent);

    public void AddAuditEvent(AuditEvent auditEvent) => _inner.AddAuditEvent(auditEvent);
}
