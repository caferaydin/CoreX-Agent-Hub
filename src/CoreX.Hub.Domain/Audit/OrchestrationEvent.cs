using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Audit;

/// <summary>
/// Small timeline row rendered by the CLI and, later, the UI (architecture R.3).
/// Deliberately carries no blob: large output lives in the evidence store (ADR-0013).
/// Phase 1 persists the shape only.
/// </summary>
public sealed class OrchestrationEvent
{
    private OrchestrationEvent()
    {
        // EF Core
    }

    public OrchestrationEvent(
        Guid id,
        Guid organizationId,
        Guid executionId,
        string eventType,
        string message,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionId = Guard.NotEmpty(executionId, nameof(executionId));
        EventType = Guard.NotBlankMax(eventType, nameof(eventType), 100);
        Message = Guard.NotBlankMax(message, nameof(message), 1000);
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionId { get; private set; }

    public string EventType { get; private set; } = null!;

    public string Message { get; private set; } = null!;

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
