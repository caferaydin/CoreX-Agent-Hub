using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>
/// Content addressed evidence storage (ADR-0013, architecture I.5).
///
/// Layout, under a Hub owned root that is never inside a source repository, an agent
/// worktree or a context repository:
///
///   blobs/&lt;sha256[0:2]&gt;/&lt;sha256&gt;
///
/// Three properties hold together:
///   - the name is the SHA-256 of exactly the bytes the file contains, so a blob can be
///     verified without any index;
///   - redaction happens before hashing, so the hash covers the stored (redacted) bytes
///     and the raw secret is never written at all;
///   - the final name only ever appears through an atomic rename, so a reader never
///     observes a partially written blob.
///
/// There is deliberately no deletion, no garbage collection and no reference counting:
/// content addressing makes safe deletion a reference counting problem, which the frozen
/// architecture defers to Phase 10.
/// </summary>
public sealed class EvidenceBlobStore
{
    public const string BlobsDirectoryName = "blobs";
    public const string PendingDirectoryName = "_pending";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly EvidenceRedactor _redactor;

    public EvidenceBlobStore(string rootDirectory, EvidenceRedactor? redactor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        Root = Path.GetFullPath(rootDirectory);
        BlobsRoot = Path.Combine(Root, BlobsDirectoryName);
        PendingRoot = Path.Combine(BlobsRoot, PendingDirectoryName);
        _redactor = redactor ?? new EvidenceRedactor();

        Directory.CreateDirectory(BlobsRoot);
        Directory.CreateDirectory(PendingRoot);
    }

    public string Root { get; }

    public string BlobsRoot { get; }

    private string PendingRoot { get; }

    /// <summary>
    /// Test seam: invoked after the temporary file is complete and before it is renamed to
    /// its content addressed name. Production leaves it null.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeFinalizeAsync { get; set; }

    public string ResolveBlobPath(string sha256)
    {
        var hash = Normalize(sha256);
        return Path.Combine(BlobsRoot, hash[..2], hash);
    }

    public bool Exists(string sha256) => File.Exists(ResolveBlobPath(sha256));

    public Task<string> ReadTextAsync(string sha256, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(ResolveBlobPath(sha256), Utf8NoBom, cancellationToken);

    /// <summary>
    /// Redacts, hashes and stores text. The raw string may exist in memory - it came from a
    /// process that already produced it - but it is never handed to the filesystem.
    /// </summary>
    public async Task<EvidenceBlobDescriptor> WriteTextAsync(
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        var redaction = _redactor.Redact(text);
        var bytes = Utf8NoBom.GetBytes(redaction.Text);

        var descriptor = await WriteBytesAsync(bytes, kind, mediaType, redaction.RedactionCount, cancellationToken);
        return descriptor;
    }

    /// <summary>
    /// Stores bytes exactly as given. Callers that hold text must go through
    /// <see cref="WriteTextAsync"/> so redaction cannot be skipped by accident.
    /// </summary>
    private async Task<EvidenceBlobDescriptor> WriteBytesAsync(
        byte[] bytes,
        EvidenceKind kind,
        string mediaType,
        int redactionCount,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var finalPath = ResolveBlobPath(hash);

        if (File.Exists(finalPath))
        {
            // Identical content is already stored. Content addressing makes this safe:
            // the name is the hash of the bytes, so there is nothing to overwrite.
            return Describe(hash, bytes.LongLength, kind, mediaType, redactionCount, true, finalPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        // The temporary file lives inside the blob root, so the rename below stays on one
        // volume and is therefore atomic.
        var pendingPath = Path.Combine(PendingRoot, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await using (var stream = new FileStream(
                pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (BeforeFinalizeAsync is { } hook)
            {
                await hook(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(pendingPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer stored the identical content first. Both writers agree on
                // the bytes, so converging on the existing blob is correct.
                return Describe(hash, bytes.LongLength, kind, mediaType, redactionCount, true, finalPath);
            }

            return Describe(hash, bytes.LongLength, kind, mediaType, redactionCount, false, finalPath);
        }
        finally
        {
            TryDeletePending(pendingPath);
        }
    }

    private static EvidenceBlobDescriptor Describe(
        string hash,
        long size,
        EvidenceKind kind,
        string mediaType,
        int redactionCount,
        bool deduplicated,
        string path) => new()
    {
        Sha256 = hash,
        SizeBytes = size,
        Kind = kind,
        MediaType = mediaType,
        RedactionCount = redactionCount,
        Deduplicated = deduplicated,
        BlobPath = path,
    };

    /// <summary>
    /// Debris from an interrupted write is removed where possible. A leftover pending file
    /// is never visible as a blob, because only the atomic rename creates that name.
    /// </summary>
    private static void TryDeletePending(string pendingPath)
    {
        try
        {
            if (File.Exists(pendingPath))
            {
                File.Delete(pendingPath);
            }
        }
        catch (IOException)
        {
            // Left for the operator; it cannot be mistaken for stored evidence.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Normalize(string? sha256)
    {
        var hash = (sha256 ?? string.Empty).Trim().ToLowerInvariant();

        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An evidence hash must be 64 hexadecimal characters.", nameof(sha256));
        }

        return hash;
    }
}
