using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tasks;

/// <summary>
/// Phase 1 task use cases: create, read, and the Draft -&gt; Ready transition.
///
/// Deliberately absent: everything that belongs to an execution pipeline. There is no
/// run, no accept, no reject here (architecture section 27 phase boundary).
/// </summary>
public sealed class TaskService(IHubStore store)
{
    private readonly IHubStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TaskView> CreateAsync(CreateTaskRequest request, DateTimeOffset nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var organization = await _store.FindOrganizationBySlugAsync(request.OrganizationSlug, ct)
            ?? throw new TaskOperationException($"Organization '{request.OrganizationSlug}' was not found.");

        var project = await _store.FindProjectBySlugAsync(organization.Id, request.ProjectSlug, ct)
            ?? throw new TaskOperationException(
                $"Project '{request.ProjectSlug}' was not found in organization '{organization.Slug}'.");

        var repository = await ResolveRepositoryAsync(organization, project, request.RepositorySlug, ct);

        var taskKey = await ResolveTaskKeyAsync(project, request.TaskKey, ct);

        var task = new HubTask(
            Guid.CreateVersion7(),
            organization,
            project,
            repository,
            taskKey,
            request.Objective,
            request.AllowedScope,
            request.ForbiddenScope,
            nowUtc);

        var order = 1;
        foreach (var input in request.Criteria)
        {
            task.AddAcceptanceCriterion(BuildCriterion(task, input, order++));
        }

        _store.AddTask(task);
        _store.AddAuditEvent(new AuditEvent(
            Guid.CreateVersion7(),
            organization.Id,
            eventType: "TaskCreated",
            actor: request.Actor,
            summary: $"Task {taskKey} created in {organization.Slug}/{project.Slug} ({repository.Slug}).",
            taskId: task.Id,
            executionId: null,
            correlationId: null,
            occurredAtUtc: nowUtc));

        await _store.SaveChangesAsync(ct);

        return await BuildViewAsync(task, organization, project, repository, ct);
    }

    public async Task<TaskView> GetAsync(string taskKey, string? organizationSlug, CancellationToken ct)
    {
        var task = await ResolveTaskAsync(taskKey, organizationSlug, ct);
        return await BuildViewAsync(task, ct);
    }

    public async Task<IReadOnlyList<TaskView>> ListAsync(
        string? organizationSlug,
        string? projectSlug,
        CancellationToken ct)
    {
        Guid? organizationId = null;
        Guid? projectId = null;

        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await _store.FindOrganizationBySlugAsync(organizationSlug, ct)
                ?? throw new TaskOperationException($"Organization '{organizationSlug}' was not found.");
            organizationId = organization.Id;

            if (!string.IsNullOrWhiteSpace(projectSlug))
            {
                var project = await _store.FindProjectBySlugAsync(organization.Id, projectSlug, ct)
                    ?? throw new TaskOperationException($"Project '{projectSlug}' was not found.");
                projectId = project.Id;
            }
        }

        var tasks = await _store.ListTasksAsync(organizationId, projectId, ct);

        var views = new List<TaskView>(tasks.Count);
        foreach (var task in tasks)
        {
            views.Add(await BuildViewAsync(task, ct));
        }

        return views;
    }

    /// <summary>
    /// Draft -&gt; Ready. Returns the gate result instead of throwing when the gate is not
    /// satisfied, so the caller can report every violation at once.
    /// </summary>
    public async Task<MarkReadyResult> MarkReadyAsync(
        string taskKey,
        string? organizationSlug,
        string actor,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var task = await ResolveTaskAsync(taskKey, organizationSlug, ct);
        var view = await BuildViewAsync(task, ct);

        if (!view.ReadyGate.IsSatisfied)
        {
            return new MarkReadyResult(view, Transitioned: false);
        }

        if (task.State == TaskState.Ready)
        {
            return new MarkReadyResult(view, Transitioned: false);
        }

        task.MarkReady(view.ReadyGate, nowUtc);

        _store.AddAuditEvent(new AuditEvent(
            Guid.CreateVersion7(),
            task.OrganizationId,
            eventType: "TaskMarkedReady",
            actor: actor,
            summary: $"Task {task.TaskKey} moved from Draft to Ready.",
            taskId: task.Id,
            executionId: null,
            correlationId: null,
            occurredAtUtc: nowUtc));

        await _store.SaveChangesAsync(ct);

        return new MarkReadyResult(await BuildViewAsync(task, ct), Transitioned: true);
    }

    private async Task<HubTask> ResolveTaskAsync(string taskKey, string? organizationSlug, CancellationToken ct)
    {
        if (!TaskKey.TryParse(taskKey, out var parsed))
        {
            throw new TaskOperationException(
                $"'{taskKey}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.");
        }

        var matches = await _store.FindTasksByKeyAsync(parsed.Value, ct);

        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await _store.FindOrganizationBySlugAsync(organizationSlug, ct)
                ?? throw new TaskOperationException($"Organization '{organizationSlug}' was not found.");

            matches = [.. matches.Where(t => t.OrganizationId == organization.Id)];
        }

        return matches.Count switch
        {
            0 => throw new TaskOperationException($"Task '{parsed}' was not found."),
            1 => matches[0],

            // Task keys are unique per project, and two organizations may legitimately use
            // the same project prefix (for example ERP). Disambiguation is explicit.
            _ => throw new TaskOperationException(
                $"Task key '{parsed}' matches {matches.Count} tasks. Re-run with --org to disambiguate."),
        };
    }

    private async Task<Domain.Catalog.Repository> ResolveRepositoryAsync(
        Organization organization,
        Project project,
        string? repositorySlug,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(repositorySlug))
        {
            return await _store.FindRepositoryBySlugAsync(project.Id, repositorySlug, ct)
                ?? throw new TaskOperationException(
                    $"Repository '{repositorySlug}' was not found in project '{project.Slug}'.");
        }

        var repositories = await _store.ListRepositoriesAsync(organization.Id, project.Id, ct);

        return repositories.Count switch
        {
            0 => throw new TaskOperationException($"Project '{project.Slug}' has no repositories."),
            1 => repositories[0],
            _ => throw new TaskOperationException(
                $"Project '{project.Slug}' has {repositories.Count} repositories; specify --repo."),
        };
    }

    private async Task<TaskKey> ResolveTaskKeyAsync(Project project, string? requestedKey, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requestedKey))
        {
            if (!TaskKey.TryParse(requestedKey, out var explicitKey))
            {
                throw new TaskOperationException(
                    $"'{requestedKey}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.");
            }

            if (!string.Equals(explicitKey.Prefix, project.TaskKeyPrefix, StringComparison.Ordinal))
            {
                throw new TaskOperationException(
                    $"Task key '{explicitKey}' does not match project '{project.Slug}' prefix '{project.TaskKeyPrefix}'.");
            }

            var clashes = await _store.FindTasksByKeyAsync(explicitKey.Value, ct);
            if (clashes.Any(t => t.ProjectId == project.Id))
            {
                throw new TaskOperationException($"Task key '{explicitKey}' already exists in project '{project.Slug}'.");
            }

            return explicitKey;
        }

        var existing = await _store.ListTasksAsync(project.OrganizationId, project.Id, ct);
        var next = existing.Count == 0 ? 1 : existing.Max(t => t.TaskKey.Number) + 1;

        if (next > 999)
        {
            throw new TaskOperationException(
                $"Project '{project.Slug}' has exhausted the 3 digit task key range.");
        }

        return TaskKey.Parse($"{project.TaskKeyPrefix}-{next:D3}");
    }

    private static AcceptanceCriterion BuildCriterion(HubTask task, CriterionInput input, int order) =>
        input.Kind switch
        {
            VerificationKind.Machine => AcceptanceCriterion.Machine(
                Guid.CreateVersion7(), task.OrganizationId, task.Id, order, input.Text, input.VerificationRef),
            VerificationKind.Human => AcceptanceCriterion.Human(
                Guid.CreateVersion7(), task.OrganizationId, task.Id, order, input.Text, input.HumanCheckHint),
            _ => AcceptanceCriterion.Undeclared(
                Guid.CreateVersion7(), task.OrganizationId, task.Id, order, input.Text),
        };

    private async Task<TaskView> BuildViewAsync(HubTask task, CancellationToken ct)
    {
        var organization = await _store.FindOrganizationAsync(task.OrganizationId, ct);
        var project = await _store.FindProjectAsync(task.ProjectId, ct);
        var repository = await _store.FindRepositoryAsync(task.RepositoryId, ct);

        if (organization is null || project is null || repository is null)
        {
            throw new TaskOperationException(
                $"Task '{task.TaskKey}' references catalog records that no longer exist.");
        }

        return await BuildViewAsync(task, organization, project, repository, ct);
    }

    private async Task<TaskView> BuildViewAsync(
        HubTask task,
        Organization organization,
        Project project,
        Domain.Catalog.Repository repository,
        CancellationToken ct)
    {
        var profile = await _store.FindDefaultVerificationProfileAsync(repository.Id, ct);
        var gate = ReadyGate.Evaluate(task, organization, project, repository, profile);
        return new TaskView(task, organization, project, repository, profile, gate);
    }
}
