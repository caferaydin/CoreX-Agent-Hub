using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// A single acceptance criterion. ADR-0015: every criterion must declare HOW it is
/// verified. <see cref="Kind"/> is nullable so that an incomplete criterion can exist on
/// a <see cref="TaskState.Draft"/> task; the ready gate rejects it.
/// </summary>
public sealed class AcceptanceCriterion
{
    private AcceptanceCriterion()
    {
        // EF Core
    }

    private AcceptanceCriterion(
        Guid id,
        Guid organizationId,
        Guid taskId,
        int order,
        string text,
        VerificationKind? kind,
        string? verificationRef,
        string? humanCheckHint)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        TaskId = Guard.NotEmpty(taskId, nameof(taskId));
        Order = order >= 0 ? order : throw new DomainException("Criterion order must not be negative.");
        Text = Guard.NotBlankMax(text, nameof(text), 500);
        Kind = kind;
        VerificationRef = Guard.OptionalMax(verificationRef, nameof(verificationRef), 100);
        HumanCheckHint = Guard.OptionalMax(humanCheckHint, nameof(humanCheckHint), 500);
    }

    /// <summary>Criterion resolved from a machine verification step.</summary>
    public static AcceptanceCriterion Machine(
        Guid id,
        Guid organizationId,
        Guid taskId,
        int order,
        string text,
        string? verificationRef) =>
        new(id, organizationId, taskId, order, text, VerificationKind.Machine, verificationRef, humanCheckHint: null);

    /// <summary>Criterion resolved by a human verdict. Requires no verification command.</summary>
    public static AcceptanceCriterion Human(
        Guid id,
        Guid organizationId,
        Guid taskId,
        int order,
        string text,
        string? humanCheckHint = null) =>
        new(id, organizationId, taskId, order, text, VerificationKind.Human, verificationRef: null, humanCheckHint);

    /// <summary>
    /// Criterion whose verification method has not been declared yet. Legal on a draft
    /// task, rejected by the ready gate (ADR-0015).
    /// </summary>
    public static AcceptanceCriterion Undeclared(
        Guid id,
        Guid organizationId,
        Guid taskId,
        int order,
        string text) =>
        new(id, organizationId, taskId, order, text, kind: null, verificationRef: null, humanCheckHint: null);

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid TaskId { get; private set; }

    public int Order { get; private set; }

    public string Text { get; private set; } = null!;

    public VerificationKind? Kind { get; private set; }

    /// <summary>Name of a step in the repository's selected verification profile.</summary>
    public string? VerificationRef { get; private set; }

    public string? HumanCheckHint { get; private set; }
}
