using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Composition.HardEvidence;

/// <summary>
/// Where one hard isolated organization's evidence lives.
///
/// The same content addressed convention the Windows store uses - <c>blobs/&lt;prefix&gt;/&lt;sha&gt;</c>
/// with a pending directory beside it - because the layout is not the thing that changes. What
/// changes is that there is one of these per organization, rooted inside that organization's own
/// isolation boundary, and nothing is shared between two of them.
///
/// That is a deliberate loss. A single content addressed store deduplicates identical bytes
/// across every caller, which is exactly what makes it a side channel: an organization that can
/// observe whether a hash already exists learns something about content it was never shown. Two
/// organizations storing identical bytes here store them twice, physically, and neither can tell
/// the other did.
/// </summary>
public sealed record HardEvidenceLayout
{
    private HardEvidenceLayout(string evidenceRoot)
    {
        EvidenceRoot = evidenceRoot;
        BlobsRoot = evidenceRoot + "/" + EvidenceBlobStore.BlobsDirectoryName;
        PendingRoot = BlobsRoot + "/" + EvidenceBlobStore.PendingDirectoryName;
    }

    public string EvidenceRoot { get; }

    public string BlobsRoot { get; }

    /// <summary>
    /// Where a blob lives before it has a name.
    ///
    /// Inside the blob root on purpose: the publication is a rename, and a rename is only
    /// atomic within one filesystem.
    /// </summary>
    public string PendingRoot { get; }

    public static HardEvidenceLayout For(string evidenceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);

        if (!LinuxPath.TryNormalize(evidenceRoot, out var segments, out var problem))
        {
            throw new ArgumentException(
                $"the evidence root is not usable as hard isolated storage: {problem}", nameof(evidenceRoot));
        }

        return new HardEvidenceLayout(LinuxPath.Join(segments));
    }

    /// <summary>
    /// Where the bytes with this hash live, if they live anywhere.
    ///
    /// Content identity, not path identity: the caller names a hash and gets the one place that
    /// hash can be. There is no way to ask for a path, which is why there is no traversal to
    /// defend against - <see cref="Hash"/> refuses anything that is not sixty-four hexadecimal
    /// characters before a path is built at all.
    /// </summary>
    public string Blob(string sha256)
    {
        var hash = Hash(sha256);
        return BlobsRoot + "/" + hash[..2] + "/" + hash;
    }

    /// <summary>The directory one hash's blob belongs in.</summary>
    public string Shard(string sha256) => BlobsRoot + "/" + Hash(sha256)[..2];

    /// <summary>A unique unpublished name for one write.</summary>
    public string Pending(string sha256, Guid attempt) =>
        PendingRoot + "/" + Hash(sha256) + "." + attempt.ToString("N") + ".tmp";

    /// <summary>
    /// A hash, or nothing.
    ///
    /// The single narrow point every path in this type goes through. A caller cannot supply a
    /// relative path, an absolute one, a Windows one or a <c>..</c>, because a caller cannot
    /// supply a path: it supplies sixty-four hexadecimal characters or it supplies nothing.
    /// </summary>
    public static string Hash(string? sha256)
    {
        var hash = (sha256 ?? string.Empty).Trim().ToLowerInvariant();

        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "An evidence hash must be 64 hexadecimal characters.", nameof(sha256));
        }

        return hash;
    }

    /// <summary>Whether a string is a hash, for callers that would rather not catch.</summary>
    public static bool IsHash(string? sha256) =>
        sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit);
}
