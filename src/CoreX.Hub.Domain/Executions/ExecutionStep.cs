using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// One ordered step of an execution. The architecture folds AgentRun, Verification and
/// Review into this single typed step so there is one timeline and one retry semantic.
/// Phase 1 persists the shape only.
/// </summary>
public sealed partial class ExecutionStep
{
    private ExecutionStep()
    {
        // EF Core
    }

    public ExecutionStep(
        Guid id,
        Guid organizationId,
        Guid executionId,
        int order,
        ExecutionStepKind kind,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionId = Guard.NotEmpty(executionId, nameof(executionId));
        Order = Guard.Positive(order, nameof(order));
        Kind = kind;
        State = ExecutionStepState.Pending;
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionId { get; private set; }

    public int Order { get; private set; }

    public ExecutionStepKind Kind { get; private set; }

    public ExecutionStepState State { get; private set; }

    /// <summary>Why the step was skipped or marked unsupported. Never silent (ADR-0008).</summary>
    public string? StateReason { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
