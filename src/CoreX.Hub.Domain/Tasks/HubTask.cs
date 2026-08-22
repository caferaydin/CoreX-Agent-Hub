using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// The <c>Task</c> aggregate root from the architecture document.
/// The CLR type is named <c>HubTask</c> only to avoid colliding with
/// <see cref="System.Threading.Tasks.Task"/>; the domain concept is unchanged.
/// </summary>
public sealed class HubTask
{
    private readonly List<AcceptanceCriterion> _acceptanceCriteria = [];
    private List<string> _allowedScope = [];
    private List<string> _forbiddenScope = [];

    private HubTask()
    {
        // EF Core
    }

    public HubTask(
        Guid id,
        Organization organization,
        Project project,
        Repository repository,
        TaskKey taskKey,
        string objective,
        IEnumerable<string> allowedScope,
        IEnumerable<string> forbiddenScope,
        DateTimeOffset nowUtc)
    {
        Guard.NotNull(organization, nameof(organization));
        Guard.NotNull(project, nameof(project));
        Guard.NotNull(repository, nameof(repository));
        Guard.NotNull(taskKey, nameof(taskKey));

        if (project.OrganizationId != organization.Id)
        {
            throw new DomainException(
                $"Project '{project.Slug}' does not belong to organization '{organization.Slug}'.");
        }

        if (repository.ProjectId != project.Id)
        {
            throw new DomainException(
                $"Repository '{repository.Slug}' does not belong to project '{project.Slug}'.");
        }

        if (!string.Equals(taskKey.Prefix, project.TaskKeyPrefix, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Task key '{taskKey}' does not match project '{project.Slug}' prefix '{project.TaskKeyPrefix}'.");
        }

        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = organization.Id;
        ProjectId = project.Id;
        RepositoryId = repository.Id;
        TaskKey = taskKey;
        Objective = Guard.NotBlankMax(objective, nameof(objective), 4000);
        _allowedScope = NormalizeScope(allowedScope);
        _forbiddenScope = NormalizeScope(forbiddenScope);
        State = TaskState.Draft;
        Version = 1;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RepositoryId { get; private set; }

    public TaskKey TaskKey { get; private set; } = null!;

    public string Objective { get; private set; } = null!;

    /// <summary>Globs an execution is allowed to change. Enforced by ScopeGuard (Phase 5).</summary>
    public IReadOnlyList<string> AllowedScope => _allowedScope;

    public IReadOnlyList<string> ForbiddenScope => _forbiddenScope;

    public TaskState State { get; private set; }

    /// <summary>
    /// Why a ready task cannot currently proceed. There is no separate Blocked state
    /// (architecture P.1).
    /// </summary>
    public string? BlockReason { get; private set; }

    /// <summary>Optimistic concurrency token (architecture P.6).</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria =>
        [.. _acceptanceCriteria.OrderBy(c => c.Order)];

    public void AddAcceptanceCriterion(AcceptanceCriterion criterion)
    {
        Guard.NotNull(criterion, nameof(criterion));

        if (State != TaskState.Draft)
        {
            throw new DomainException(
                $"Acceptance criteria can only be edited while the task is in {TaskState.Draft}.");
        }

        if (criterion.TaskId != Id)
        {
            throw new DomainException("Acceptance criterion belongs to a different task.");
        }

        if (_acceptanceCriteria.Any(c => c.Order == criterion.Order))
        {
            throw new DomainException($"Acceptance criterion order {criterion.Order} is already used.");
        }

        _acceptanceCriteria.Add(criterion);
    }

    public int NextCriterionOrder =>
        _acceptanceCriteria.Count == 0 ? 1 : _acceptanceCriteria.Max(c => c.Order) + 1;

    public void SetBlockReason(string? reason, DateTimeOffset nowUtc)
    {
        BlockReason = Guard.OptionalMax(reason, nameof(reason), 500);
        Touch(nowUtc);
    }

    /// <summary>
    /// Draft -&gt; Ready. The only task lifecycle behaviour exercised in Phase 1.
    /// The caller must pass a satisfied ready gate result; the gate itself lives in
    /// <see cref="ReadyGate"/> so it can be evaluated and reported without mutating state.
    /// </summary>
    public void MarkReady(ReadyGateResult gateResult, DateTimeOffset nowUtc)
    {
        Guard.NotNull(gateResult, nameof(gateResult));

        if (State == TaskState.Ready)
        {
            return;
        }

        if (!TaskStateRules.CanTransition(State, TaskState.Ready))
        {
            throw new DomainException($"Task '{TaskKey}' cannot move from {State} to {TaskState.Ready}.");
        }

        if (!gateResult.IsSatisfied)
        {
            throw new DomainException(
                $"Task '{TaskKey}' does not satisfy the ready gate: {gateResult.Describe()}");
        }

        State = TaskState.Ready;
        BlockReason = null;
        Touch(nowUtc);
    }

    private void Touch(DateTimeOffset nowUtc)
    {
        Version++;
        UpdatedAtUtc = nowUtc;
    }

    private static List<string> NormalizeScope(IEnumerable<string> values) =>
        [.. (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)];
}
