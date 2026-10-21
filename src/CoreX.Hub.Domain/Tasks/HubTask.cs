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

    /// <summary>An execution has taken this task. Only a Ready task can be taken.</summary>
    public void MarkRunning(DateTimeOffset nowUtc) => Transition(TaskState.Running, nowUtc);

    /// <summary>
    /// The execution passed and a human now has to decide. This is the only edge out of
    /// Running that means success, and it stops here: Accepted belongs to a person.
    /// </summary>
    public void MarkAwaitingAcceptance(DateTimeOffset nowUtc)
    {
        if (State != TaskState.Running)
        {
            throw new DomainException($"Task '{TaskKey}' is {State}, so no execution can be awaiting acceptance.");
        }

        Transition(TaskState.AwaitingAcceptance, nowUtc);
    }

    /// <summary>
    /// The execution ended without passing, so the task becomes available again. Failure
    /// lives on the execution: there is deliberately no failed task.
    /// </summary>
    public void ReturnToReady(string? blockReason, DateTimeOffset nowUtc)
    {
        Transition(TaskState.Ready, nowUtc);
        BlockReason = Guard.OptionalMax(blockReason, nameof(blockReason), 1000);
    }

    /// <summary>
    /// The work was accepted after an execution passed.
    ///
    /// The only edge into <see cref="TaskState.Accepted"/> that a passing run can take. A
    /// person takes it, or - since ADR-0028 - a repository's autopilot does, and only
    /// through the acceptance service, for work whose evidence answers every criterion. The
    /// orchestrator never reaches this method, and acceptance itself means a decision was
    /// recorded, never that anything was merged, pushed or released.
    /// </summary>
    public void Accept(DateTimeOffset nowUtc)
    {
        if (State != TaskState.AwaitingAcceptance)
        {
            throw new DomainException($"Task '{TaskKey}' is {State}, so there is nothing awaiting acceptance.");
        }

        Transition(TaskState.Accepted, nowUtc);
    }

    /// <summary>
    /// A person accepted the work although review blocked it (ADR-0014 / errata E2).
    ///
    /// This is the one edge <see cref="Transition"/> refuses on purpose, because reaching
    /// Accepted from Ready is meaningless unless a blocking review is what put the task
    /// there. Whether that was really the case is decided before this is called; what the
    /// aggregate insists on is that the edge cannot be taken by accident.
    /// </summary>
    public void AcceptByReviewOverride(DateTimeOffset nowUtc)
    {
        if (State != TaskState.Ready)
        {
            throw new DomainException(
                $"Task '{TaskKey}' is {State}; a review override acceptance starts from Ready.");
        }

        State = TaskState.Accepted;
        BlockReason = null;
        Touch(nowUtc);
    }

    /// <summary>
    /// A person rejected the work. Terminal: the task is finished with, and the execution
    /// that produced it keeps whatever outcome it earned.
    /// </summary>
    public void Reject(string? reason, DateTimeOffset nowUtc)
    {
        if (State != TaskState.AwaitingAcceptance)
        {
            throw new DomainException($"Task '{TaskKey}' is {State}, so there is nothing awaiting a decision.");
        }

        Transition(TaskState.Rejected, nowUtc);
        BlockReason = Guard.OptionalMax(reason, nameof(reason), 1000);
    }

    /// <summary>
    /// A person abandoned the task. Terminal, and deliberately not the same thing as an
    /// execution ending Cancelled.
    ///
    /// An execution is cancelled when its caller stopped it; that is a fact about one
    /// attempt, and the task normally becomes available again afterwards so somebody can
    /// try once more. This is the other decision - the work itself is not wanted - and only
    /// an operator makes it. Nothing in orchestration reaches this method, which is why a
    /// stopped attempt cannot quietly end the task.
    ///
    /// Whatever the executions produced keeps whatever it earned: nothing here rewrites an
    /// execution, drops a commit or touches a worktree.
    /// </summary>
    public void CancelByOperator(string? reason, DateTimeOffset nowUtc)
    {
        if (TaskStateRules.IsTerminal(State))
        {
            throw new DomainException($"Task '{TaskKey}' is already {State}, so it cannot be cancelled.");
        }

        Transition(TaskState.Cancelled, nowUtc);
        BlockReason = Guard.OptionalMax(reason, nameof(reason), 1000);
    }

    private void Transition(TaskState next, DateTimeOffset nowUtc)
    {
        if (!TaskStateRules.CanTransition(State, next))
        {
            throw new DomainException($"Task '{TaskKey}' cannot move from {State} to {next}.");
        }

        if (TaskStateRules.RequiresReviewOverride(State, next))
        {
            throw new DomainException(
                $"Task '{TaskKey}' may only reach {next} from {State} through a review override acceptance.");
        }

        State = next;
        Touch(nowUtc);
    }

    private void Touch(DateTimeOffset nowUtc)
    {
        Version++;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Scope entries are parsed by the domain value object, so a malformed one cannot
    /// exist on a task at all - let alone survive until a gate discovers, after an agent
    /// has run, that the task's own policy was unreadable. The stored representation stays
    /// textual; ScopeGlob owns what that text means.
    /// </summary>
    private static List<string> NormalizeScope(IEnumerable<string> values) =>
        [.. (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => ScopeGlob.Parse(v).Value)
            .Distinct(StringComparer.Ordinal)];
}
