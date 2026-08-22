using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
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

    void AddAuditEvent(AuditEvent auditEvent);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
