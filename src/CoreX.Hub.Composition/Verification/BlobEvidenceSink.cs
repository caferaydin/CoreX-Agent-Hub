using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Composition.Verification;

/// <summary>
/// Composition layer adapter joining the verification module to the content addressed
/// evidence store. Redaction happens inside the store, before anything is written, so this
/// adapter has no way to persist raw text even by mistake.
/// </summary>
public sealed class BlobEvidenceSink : IVerificationEvidenceSink
{
    private readonly EvidenceBlobStore _store;

    public BlobEvidenceSink(EvidenceBlobStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<EvidenceRef> WriteTextAsync(
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var descriptor = await _store.WriteTextAsync(text, kind, mediaType, cancellationToken);

        return new EvidenceRef
        {
            Sha256 = descriptor.Sha256,
            SizeBytes = descriptor.SizeBytes,
            Kind = descriptor.Kind,
            MediaType = descriptor.MediaType,
            RedactionCount = descriptor.RedactionCount,
        };
    }
}
