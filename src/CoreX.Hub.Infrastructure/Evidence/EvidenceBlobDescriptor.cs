using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>
/// What the store knows about one persisted blob. The shape matches
/// <see cref="EvidenceArtifact"/> so a caller with a valid execution context can persist
/// the metadata row without translation - and the blob itself never enters the database.
/// </summary>
public sealed record EvidenceBlobDescriptor
{
    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required EvidenceKind Kind { get; init; }

    public required string MediaType { get; init; }

    public required int RedactionCount { get; init; }

    /// <summary>True when identical bytes were already stored, so nothing new was written.</summary>
    public required bool Deduplicated { get; init; }

    public required string BlobPath { get; init; }
}
