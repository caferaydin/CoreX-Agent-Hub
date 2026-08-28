using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Composition.HardEvidence;

/// <summary>
/// The evidence writer for a hard isolated organization.
///
/// The same Application port the Windows one answers, so nothing above this line learns which
/// side of the boundary the bytes went to. Which store an organization gets is decided from the
/// organization - never from a provider, a task objective, a request body or a caller supplied
/// path - and there is no argument here through which it could be.
///
/// Redaction is the shared one. <see cref="EvidenceRedactor"/> carries the single secret pattern
/// catalogue, and it runs before the bytes are hashed, so the hash covers the stored text and a
/// raw secret is never written at all. A second catalogue for hard organizations would be a
/// second policy, and the weaker of two would decide what a secret is.
///
/// This exists as a primitive. Nothing composes it into an execution yet: a hard task also needs
/// its network boundary, and admitting one because its evidence is isolated would leave the rest
/// of the work outside.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardEvidenceGateway : IExecutionEvidenceWriter
{
    private readonly HardEvidenceStore _store;
    private readonly EvidenceRedactor _redactor;
    private readonly Isolation.IHardExecutionBinding _binding;

    internal HardEvidenceGateway(
        HardEvidenceStore store,
        Isolation.IHardExecutionBinding binding,
        EvidenceRedactor? redactor = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _redactor = redactor ?? new EvidenceRedactor();
    }

    /// <summary>
    /// Builds the writer for one organization's environment, bound to one execution's routing.
    ///
    /// The binding is required rather than optional. One hard execution writes to one hard evidence
    /// backend, and an evidence writer that could not say which execution it served would be the
    /// thing that made "artifact by artifact" routing possible.
    /// </summary>
    internal static HardEvidenceGateway Create(
        HardIsolationProfile profile,
        string hostWorkingDirectory,
        Isolation.IHardExecutionBinding binding) =>
        new(HardEvidenceStore.Create(profile, hostWorkingDirectory), binding);

    /// <summary>Creates and closes the store. Idempotent; safe to call before every execution.</summary>
    public Task<bool> EnsureAsync(CancellationToken ct) => _store.EnsureAsync(ct);

    /// <summary>Reads evidence back by content identity, for the trusted control plane only.</summary>
    public Task<string?> ReadTextAsync(string sha256, CancellationToken ct) =>
        _store.ReadTextAsync(sha256, ct);

    public async Task<StoredEvidence?> WriteAsync(
        Guid executionId,
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken ct)
    {
        // Whose evidence this is, checked against everything else this composition has routed. No
        // live boundary is taken: the isolated store is reached through the trusted service identity
        // and has never needed a network namespace, so acquiring one here would be spending a real
        // resource to satisfy a bookkeeping rule.
        _binding.BindTo(executionId);

        if (string.IsNullOrEmpty(text))
        {
            // An empty blob is not evidence of anything, and the Windows writer agrees.
            return null;
        }

        var redaction = _redactor.Redact(text);
        var stored = await _store.WriteTextAsync(redaction.Text, ct);

        return stored is null
            ? null
            : new StoredEvidence(stored.Sha256, stored.SizeBytes, mediaType, redaction.RedactionCount);
    }
}
