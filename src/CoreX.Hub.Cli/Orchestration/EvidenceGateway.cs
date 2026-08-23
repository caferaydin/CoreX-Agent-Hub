using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Composition layer adapter joining the orchestrator to the Phase 4 evidence store.
///
/// Redaction happens inside the store, before the bytes are hashed or written, so the
/// orchestrator has no way to persist a raw secret even if it tried. Empty text stores
/// nothing: an empty blob is not evidence of anything.
/// </summary>
public sealed class EvidenceGateway : IExecutionEvidenceWriter
{
    private readonly EvidenceBlobStore _store;

    public EvidenceGateway(EvidenceBlobStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<StoredEvidence?> WriteAsync(
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var descriptor = await _store.WriteTextAsync(text, kind, mediaType, ct);

        return new StoredEvidence(
            descriptor.Sha256, descriptor.SizeBytes, descriptor.MediaType, descriptor.RedactionCount);
    }
}
