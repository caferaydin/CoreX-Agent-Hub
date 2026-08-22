using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Verification.Abstractions;

/// <summary>
/// A pointer to one stored evidence blob. The verification result carries these instead of
/// duplicating large output strings, and the shape matches what
/// <see cref="EvidenceArtifact"/> persists.
/// </summary>
public sealed record EvidenceRef
{
    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required EvidenceKind Kind { get; init; }

    public required string MediaType { get; init; }

    /// <summary>How many secret pattern replacements were applied before the bytes were stored.</summary>
    public required int RedactionCount { get; init; }

    public string Describe() =>
        $"{Kind} {Sha256[..12]} ({SizeBytes} bytes, {RedactionCount} redactions)";
}

/// <summary>
/// The Verification owned port for persisting evidence.
///
/// The implementation redacts before it writes: the raw text may exist in memory while a
/// process runs, but the unredacted bytes are never handed to persistent storage
/// (architecture I.5, Phase 4 brief section 30).
/// </summary>
public interface IVerificationEvidenceSink
{
    Task<EvidenceRef> WriteTextAsync(
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken cancellationToken);
}
