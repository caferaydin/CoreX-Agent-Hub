using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Abstractions;

/// <summary>
/// The single persistence port for Phase 1 (architecture section 28: no repository
/// pattern abstraction explosion). One facade, one implementation, no generic
/// repositories and no specification framework.
/// </summary>
public interface IHubStore
{
    Task<IReadOnlyList<Organization>> ListOrganizationsAsync(CancellationToken ct);

    Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken ct);

    Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct);

    Task<IReadOnlyList<Project>> ListProjectsAsync(Guid? organizationId, CancellationToken ct);

    Task<Project?> FindProjectAsync(Guid id, CancellationToken ct);

    Task<Project?> FindProjectBySlugAsync(Guid organizationId, string slug, CancellationToken ct);

    Task<IReadOnlyList<Repository>> ListRepositoriesAsync(
        Guid? organizationId,
        Guid? projectId,
        CancellationToken ct);

    Task<Repository?> FindRepositoryAsync(Guid id, CancellationToken ct);

    Task<Repository?> FindRepositoryBySlugAsync(Guid projectId, string slug, CancellationToken ct);

    Task<IReadOnlyList<VerificationProfile>> ListVerificationProfilesAsync(
        Guid repositoryId,
        CancellationToken ct);

    Task<VerificationProfile?> FindDefaultVerificationProfileAsync(
        Guid repositoryId,
        CancellationToken ct);

    Task<IReadOnlyList<AgentProfile>> ListAgentProfilesAsync(Guid organizationId, CancellationToken ct);

    Task<AgentProfile?> FindAgentProfileByNameAsync(Guid organizationId, string name, CancellationToken ct);

    Task<IReadOnlyList<HubTask>> FindTasksByKeyAsync(string taskKey, CancellationToken ct);

    Task<HubTask?> FindTaskAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<HubTask>> ListTasksAsync(Guid? organizationId, Guid? projectId, CancellationToken ct);

    void AddOrganization(Organization organization);

    void AddProject(Project project);

    void AddRepository(Repository repository);

    void AddVerificationProfile(VerificationProfile profile);

    void AddAgentProfile(AgentProfile profile);

    void AddTask(HubTask task);

    Task<Execution?> FindExecutionAsync(Guid id, CancellationToken ct);

    /// <summary>Executions for one task, newest attempt first.</summary>
    Task<IReadOnlyList<Execution>> ListExecutionsForTaskAsync(Guid taskId, CancellationToken ct);

    /// <summary>
    /// Every execution that has not reached a terminal state, whichever task it belongs to.
    /// Used by the reconciler and by the single concurrent execution rule.
    /// </summary>
    Task<IReadOnlyList<Execution>> ListNonTerminalExecutionsAsync(CancellationToken ct);

    Task<IReadOnlyList<ExecutionStep>> ListExecutionStepsAsync(Guid executionId, CancellationToken ct);

    /// <summary>
    /// The verification commands this execution actually ran, in order.
    ///
    /// Reached through the execution's own Verify step rather than by querying commands at
    /// large, because that relationship is the whole point: these are one execution's
    /// machine truth, and a baseline measured elsewhere must never appear among them.
    /// </summary>
    Task<IReadOnlyList<CommandResult>> ListVerificationCommandsAsync(Guid executionId, CancellationToken ct);

    /// <summary>
    /// Reviewer findings recorded against this execution's steps.
    ///
    /// Read for one reason only: a review override has to prove there really was something
    /// to override. Nothing on this path may change them.
    /// </summary>
    Task<IReadOnlyList<Finding>> ListFindingsAsync(Guid executionId, CancellationToken ct);

    Task<IReadOnlyList<EvidenceArtifact>> ListEvidenceAsync(Guid executionId, CancellationToken ct);

    Task<IReadOnlyList<OrchestrationEvent>> ListOrchestrationEventsAsync(Guid executionId, CancellationToken ct);

    Task<int> CountAcceptanceDecisionsAsync(Guid executionId, CancellationToken ct);

    /// <summary>The one immutable human decision on this execution, if it has been made.</summary>
    Task<AcceptanceDecision?> FindAcceptanceDecisionAsync(Guid executionId, CancellationToken ct);

    void AddExecution(Execution execution);

    void AddExecutionStep(ExecutionStep step);

    void AddCommandResult(CommandResult commandResult);

    void AddFinding(Finding finding);

    void AddAcceptanceDecision(AcceptanceDecision decision);

    void AddEvidenceArtifact(EvidenceArtifact artifact);

    void AddOrchestrationEvent(OrchestrationEvent orchestrationEvent);

    void AddAuditEvent(AuditEvent auditEvent);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
