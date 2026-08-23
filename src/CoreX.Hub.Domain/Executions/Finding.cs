using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Structured reviewer finding (architecture I.1 tier B, J.3).
/// Free-text review output is never parsed; findings arrive schema-validated.
/// Phase 1 persists the shape only.
/// </summary>
public sealed class Finding
{
    private Finding()
    {
        // EF Core
    }

    public Finding(
        Guid id,
        Guid organizationId,
        Guid executionStepId,
        FindingSeverity severity,
        FindingCategory category,
        string title,
        string rationale,
        string? file,
        int? line,
        string? suggestedFix,
        FindingConfidence confidence,
        bool outOfScope,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionStepId = Guard.NotEmpty(executionStepId, nameof(executionStepId));
        Severity = severity;
        Category = category;
        Title = Guard.NotBlankMax(title, nameof(title), 80);
        Rationale = Guard.NotBlankMax(rationale, nameof(rationale), 2000);
        File = Guard.OptionalMax(file, nameof(file), 400);
        Line = line;
        SuggestedFix = Guard.OptionalMax(suggestedFix, nameof(suggestedFix), 2000);
        Confidence = confidence;
        OutOfScope = outOfScope;
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionStepId { get; private set; }

    public FindingSeverity Severity { get; private set; }

    public FindingCategory Category { get; private set; }

    public string Title { get; private set; } = null!;

    public string Rationale { get; private set; } = null!;

    public string? File { get; private set; }

    public int? Line { get; private set; }

    public string? SuggestedFix { get; private set; }

    public FindingConfidence Confidence { get; private set; }

    /// <summary>Anchored outside the diff; recorded as an observation and never blocking.</summary>
    public bool OutOfScope { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Whether this finding is counted when the review is judged.
    ///
    /// Deliberately not called "blocking". Whether a review blocks is a property of the
    /// whole set - one Critical does it alone, Major does it by count - so no single
    /// finding can answer that question, and a property claiming to would be wrong the
    /// moment the threshold moved. <see cref="ReviewFindingGate"/> owns the judgement;
    /// this only says whether this finding takes part in it.
    /// </summary>
    public bool ParticipatesInReview => !OutOfScope;
}
