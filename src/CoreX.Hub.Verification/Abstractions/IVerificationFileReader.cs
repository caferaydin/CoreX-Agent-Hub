namespace CoreX.Hub.Verification.Abstractions;

/// <summary>Outcome of a managed path trust decision, as seen by the verification module.</summary>
public sealed record VerificationPathDecision(bool IsAllowed, string? CanonicalPath, string? Reason)
{
    public static VerificationPathDecision Allowed(string canonicalPath) => new(true, canonicalPath, null);

    public static VerificationPathDecision Rejected(string reason) => new(false, null, reason);
}

/// <summary>Why a bounded report read did not produce text.</summary>
public enum VerificationReadFailure
{
    NotFound,
    TooLarge,
    Unreadable,
}

public sealed record VerificationReadResult(string? Text, long SizeBytes, VerificationReadFailure? Failure, string? Detail)
{
    public bool Succeeded => Failure is null && Text is not null;

    public static VerificationReadResult Ok(string text, long size) => new(text, size, null, null);

    public static VerificationReadResult Failed(VerificationReadFailure failure, long size, string detail) =>
        new(null, size, failure, detail);
}

/// <summary>
/// The Verification owned port for managed path trust and bounded report reading.
///
/// E.1 #2 says canonicalisation, root containment and reparse trust belong to
/// FileSystemGuard; E.1 #4 says this module may not reference the execution module. Both
/// hold at once: verification asks through this port and the composition layer answers
/// with FileSystemGuard.
/// </summary>
public interface IVerificationFileReader
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="trustRoot"/>. A path
    /// that leaves the root, or that traverses a reparse point pointing outside it, is
    /// rejected rather than normalised.
    ///
    /// Asynchronous because containment is a fact about a filesystem, and the filesystem holding a
    /// hard organization's workspace is not the one this process can see. Deciding it there rather
    /// than approximating it here is what keeps a symlink out of a peer's workspace from reading as
    /// contained.
    /// </summary>
    Task<VerificationPathDecision> ResolveUnderRootAsync(
        string trustRoot,
        string relativePath,
        bool mustExist,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads an already validated absolute path, refusing anything larger than
    /// <paramref name="maxBytes"/> instead of growing without bound.
    /// </summary>
    Task<VerificationReadResult> ReadTextAsync(
        string absolutePath,
        long maxBytes,
        CancellationToken cancellationToken);
}
