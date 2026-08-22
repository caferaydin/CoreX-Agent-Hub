using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Metadata row pointing at a blob in the content addressed evidence store (ADR-0013).
/// The blob itself never lives in the relational database.
/// Phase 1 persists the shape only; the store lands in Phase 4.
/// </summary>
public sealed class EvidenceArtifact
{
    private EvidenceArtifact()
    {
        // EF Core
    }

    public EvidenceArtifact(
        Guid id,
        Guid organizationId,
        Guid executionId,
        Guid? executionStepId,
        EvidenceKind kind,
        string sha256,
        long sizeBytes,
        string mediaType,
        int redactionCount,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionId = Guard.NotEmpty(executionId, nameof(executionId));
        ExecutionStepId = executionStepId;
        Kind = kind;
        Sha256 = NormalizeHash(sha256);
        SizeBytes = sizeBytes >= 0
            ? sizeBytes
            : throw new DomainException("Evidence size must not be negative.");
        MediaType = Guard.NotBlankMax(mediaType, nameof(mediaType), 100);
        RedactionCount = redactionCount >= 0
            ? redactionCount
            : throw new DomainException("Redaction count must not be negative.");
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionId { get; private set; }

    public Guid? ExecutionStepId { get; private set; }

    public EvidenceKind Kind { get; private set; }

    public string Sha256 { get; private set; } = null!;

    public long SizeBytes { get; private set; }

    public string MediaType { get; private set; } = null!;

    /// <summary>How many secret-pattern redactions were applied while writing the blob.</summary>
    public int RedactionCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private static string NormalizeHash(string? value)
    {
        var hash = Guard.NotBlankMax(value, "sha256", 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new DomainException("Evidence sha256 must be 64 hexadecimal characters.");
        }

        return hash.ToLowerInvariant();
    }
}
