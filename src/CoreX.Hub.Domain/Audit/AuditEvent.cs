using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Audit;

/// <summary>
/// Append-only audit record (architecture section 20 / R). Never updated, never deleted.
/// <see cref="OrganizationId"/> is nullable so that system level events (for example a
/// configuration seed) can be recorded without inventing an owning organization.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
        // EF Core
    }

    public AuditEvent(
        Guid id,
        Guid? organizationId,
        string eventType,
        string actor,
        string summary,
        Guid? taskId,
        Guid? executionId,
        string? correlationId,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = organizationId;
        EventType = Guard.NotBlankMax(eventType, nameof(eventType), 100);
        Actor = Guard.NotBlankMax(actor, nameof(actor), 200);
        Summary = Guard.NotBlankMax(summary, nameof(summary), 2000);
        TaskId = taskId;
        ExecutionId = executionId;
        CorrelationId = Guard.OptionalMax(correlationId, nameof(correlationId), 100);
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public string EventType { get; private set; } = null!;

    public string Actor { get; private set; } = null!;

    public string Summary { get; private set; } = null!;

    public Guid? TaskId { get; private set; }

    public Guid? ExecutionId { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
