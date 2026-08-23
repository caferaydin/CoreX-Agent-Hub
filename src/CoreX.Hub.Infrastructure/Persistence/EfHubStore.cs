using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>
/// The single EF Core implementation of <see cref="IHubStore"/>.
/// No generic repository, no specification pattern, no unit-of-work abstraction beyond
/// <see cref="SaveChangesAsync"/> (architecture section 28).
/// </summary>
public sealed class EfHubStore(HubDbContext db) : IHubStore
{
    private readonly HubDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<Organization>> ListOrganizationsAsync(CancellationToken ct) =>
        await _db.Organizations.OrderBy(o => o.Slug).ToListAsync(ct);

    public async Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken ct) =>
        await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct)
    {
        if (!Slug.TryParse(slug, out var parsed))
        {
            return null;
        }

        return await _db.Organizations.FirstOrDefaultAsync(o => o.Slug == parsed, ct);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(Guid? organizationId, CancellationToken ct)
    {
        var query = _db.Projects.AsQueryable();
        if (organizationId is { } orgId)
        {
            query = query.Where(p => p.OrganizationId == orgId);
        }

        return await query.OrderBy(p => p.Slug).ToListAsync(ct);
    }

    public async Task<Project?> FindProjectAsync(Guid id, CancellationToken ct) =>
        await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Project?> FindProjectBySlugAsync(Guid organizationId, string slug, CancellationToken ct)
    {
        if (!Slug.TryParse(slug, out var parsed))
        {
            return null;
        }

        return await _db.Projects
            .FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.Slug == parsed, ct);
    }

    public async Task<IReadOnlyList<Domain.Catalog.Repository>> ListRepositoriesAsync(
        Guid? organizationId,
        Guid? projectId,
        CancellationToken ct)
    {
        var query = _db.Repositories.AsQueryable();
        if (organizationId is { } orgId)
        {
            query = query.Where(r => r.OrganizationId == orgId);
        }

        if (projectId is { } projId)
        {
            query = query.Where(r => r.ProjectId == projId);
        }

        return await query.OrderBy(r => r.Slug).ToListAsync(ct);
    }

    public async Task<Domain.Catalog.Repository?> FindRepositoryAsync(Guid id, CancellationToken ct) =>
        await _db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<Domain.Catalog.Repository?> FindRepositoryBySlugAsync(
        Guid projectId,
        string slug,
        CancellationToken ct)
    {
        if (!Slug.TryParse(slug, out var parsed))
        {
            return null;
        }

        return await _db.Repositories
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Slug == parsed, ct);
    }

    public async Task<IReadOnlyList<VerificationProfile>> ListVerificationProfilesAsync(
        Guid repositoryId,
        CancellationToken ct) =>
        await _db.VerificationProfiles
            .Where(p => p.RepositoryId == repositoryId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

    public async Task<VerificationProfile?> FindDefaultVerificationProfileAsync(
        Guid repositoryId,
        CancellationToken ct) =>
        await _db.VerificationProfiles
            .FirstOrDefaultAsync(p => p.RepositoryId == repositoryId && p.IsDefault, ct);

    public async Task<IReadOnlyList<AgentProfile>> ListAgentProfilesAsync(
        Guid organizationId,
        CancellationToken ct) =>
        await _db.AgentProfiles
            .Where(a => a.OrganizationId == organizationId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

    public async Task<AgentProfile?> FindAgentProfileByNameAsync(
        Guid organizationId,
        string name,
        CancellationToken ct) =>
        await _db.AgentProfiles
            .FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Name == name, ct);

    public async Task<IReadOnlyList<HubTask>> FindTasksByKeyAsync(string taskKey, CancellationToken ct)
    {
        if (!TaskKey.TryParse(taskKey, out var parsed))
        {
            return [];
        }

        return await TaskQuery().Where(t => t.TaskKey == parsed).ToListAsync(ct);
    }

    public async Task<HubTask?> FindTaskAsync(Guid id, CancellationToken ct) =>
        await TaskQuery().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<HubTask>> ListTasksAsync(
        Guid? organizationId,
        Guid? projectId,
        CancellationToken ct)
    {
        var query = TaskQuery();
        if (organizationId is { } orgId)
        {
            query = query.Where(t => t.OrganizationId == orgId);
        }

        if (projectId is { } projId)
        {
            query = query.Where(t => t.ProjectId == projId);
        }

        return await query.ToListAsync(ct);
    }

    public void AddOrganization(Organization organization) => _db.Organizations.Add(organization);

    public void AddProject(Project project) => _db.Projects.Add(project);

    public void AddRepository(Domain.Catalog.Repository repository) => _db.Repositories.Add(repository);

    public void AddVerificationProfile(VerificationProfile profile) => _db.VerificationProfiles.Add(profile);

    public void AddAgentProfile(AgentProfile profile) => _db.AgentProfiles.Add(profile);

    public void AddTask(HubTask task) => _db.Tasks.Add(task);

    public async Task<Execution?> FindExecutionAsync(Guid id, CancellationToken ct) =>
        await db.Executions.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Execution>> ListExecutionsForTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.Executions
            .Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.AttemptNo)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Execution>> ListNonTerminalExecutionsAsync(CancellationToken ct)
    {
        // SQLite cannot order by DateTimeOffset, so the ordering happens here rather than
        // in SQL. These lists are per execution and small.
        var executions = await db.Executions
            .Where(e => ExecutionStateRules.NonTerminalStates.Contains(e.State))
            .ToListAsync(ct);

        return [.. executions.OrderBy(e => e.CreatedAtUtc)];
    }

    public async Task<IReadOnlyList<ExecutionStep>> ListExecutionStepsAsync(Guid executionId, CancellationToken ct) =>
        await db.ExecutionSteps
            .Where(s => s.ExecutionId == executionId)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

    /// <summary>
    /// Execution to Verify step to commands, in that order.
    ///
    /// The join is the guarantee: a baseline is measured in a disposable tree and belongs to
    /// no execution's Verify step, so it cannot reach this result however similar its step
    /// names are.
    /// </summary>
    public async Task<IReadOnlyList<CommandResult>> ListVerificationCommandsAsync(
        Guid executionId,
        CancellationToken ct)
    {
        var verifySteps = await db.ExecutionSteps
            .Where(s => s.ExecutionId == executionId && s.Kind == ExecutionStepKind.Verify)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (verifySteps.Count == 0)
        {
            return [];
        }

        var commands = await db.CommandResults
            .Where(c => verifySteps.Contains(c.ExecutionStepId))
            .ToListAsync(ct);

        return [.. commands.OrderBy(c => c.Order)];
    }

    public async Task<IReadOnlyList<EvidenceArtifact>> ListEvidenceAsync(Guid executionId, CancellationToken ct)
    {
        var artifacts = await db.EvidenceArtifacts
            .Where(a => a.ExecutionId == executionId)
            .ToListAsync(ct);

        return [.. artifacts.OrderBy(a => a.CreatedAtUtc)];
    }

    public async Task<IReadOnlyList<OrchestrationEvent>> ListOrchestrationEventsAsync(
        Guid executionId,
        CancellationToken ct)
    {
        var events = await db.OrchestrationEvents
            .Where(e => e.ExecutionId == executionId)
            .ToListAsync(ct);

        return [.. events.OrderBy(e => e.OccurredAtUtc)];
    }

    public async Task<int> CountAcceptanceDecisionsAsync(Guid executionId, CancellationToken ct) =>
        await db.AcceptanceDecisions.CountAsync(d => d.ExecutionId == executionId, ct);

    public void AddExecution(Execution execution) => db.Executions.Add(execution);

    public void AddExecutionStep(ExecutionStep step) => db.ExecutionSteps.Add(step);

    public void AddCommandResult(CommandResult commandResult) => db.CommandResults.Add(commandResult);

    public void AddEvidenceArtifact(EvidenceArtifact artifact) => db.EvidenceArtifacts.Add(artifact);

    public void AddOrchestrationEvent(OrchestrationEvent orchestrationEvent) =>
        db.OrchestrationEvents.Add(orchestrationEvent);

    public void AddAuditEvent(AuditEvent auditEvent) => _db.AuditEvents.Add(auditEvent);

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    private IQueryable<HubTask> TaskQuery() =>
        _db.Tasks.Include(TaskModel.AcceptanceCriteriaNavigation).OrderBy(t => t.TaskKey);
}
