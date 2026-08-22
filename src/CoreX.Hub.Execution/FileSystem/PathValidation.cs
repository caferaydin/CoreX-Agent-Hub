namespace CoreX.Hub.Execution.FileSystem;

/// <summary>Why a managed execution path was refused. Never a bare exception message.</summary>
public enum PathRejectionReason
{
    /// <summary>Empty, malformed, or not representable as a filesystem path.</summary>
    InvalidPath,

    /// <summary>Syntactically valid but not a supported shape (UNC, device path, ...).</summary>
    UnsupportedPath,

    /// <summary>Trust root is missing, relative, or otherwise unusable.</summary>
    MissingRoot,

    /// <summary>Canonicalises to somewhere outside the trust root.</summary>
    OutsideRoot,

    /// <summary>Uses <c>..</c> segments to climb out of the trust root.</summary>
    TraversalEscape,

    /// <summary>A symbolic link, junction or other reparse point leaves the trust root.</summary>
    ReparsePointEscape,

    /// <summary>The caller required an existing path and it does not exist.</summary>
    MissingRequiredPath,
}

/// <summary>
/// Whether the caller is pointing at something that must already exist, or at a path a
/// later phase intends to create.
/// </summary>
public enum PathIntent
{
    /// <summary>The path must exist right now.</summary>
    ExistingPath,

    /// <summary>The path need not exist; every existing ancestor is still validated.</summary>
    ProposedPath,
}

/// <summary>Outcome of one path trust decision.</summary>
public sealed record PathValidationResult
{
    private PathValidationResult(bool isAllowed, string? canonicalPath, PathRejectionReason? reason, string? detail)
    {
        IsAllowed = isAllowed;
        CanonicalPath = canonicalPath;
        Reason = reason;
        Detail = detail;
    }

    public bool IsAllowed { get; }

    /// <summary>
    /// The physically resolved absolute path. Present only when <see cref="IsAllowed"/>.
    /// </summary>
    public string? CanonicalPath { get; }

    public PathRejectionReason? Reason { get; }

    public string? Detail { get; }

    public static PathValidationResult Allowed(string canonicalPath) =>
        new(true, canonicalPath, null, null);

    public static PathValidationResult Rejected(PathRejectionReason reason, string detail) =>
        new(false, null, reason, detail);

    /// <summary>Returns the canonical path or throws with the recorded reason.</summary>
    public string Require()
    {
        if (!IsAllowed)
        {
            throw new PathTrustException(Reason ?? PathRejectionReason.InvalidPath, Detail ?? "Path refused.");
        }

        return CanonicalPath!;
    }
}

/// <summary>Thrown when a caller demands a path the guard refused.</summary>
public sealed class PathTrustException : Exception
{
    public PathTrustException(PathRejectionReason reason, string message) : base(message) => Reason = reason;

    public PathRejectionReason Reason { get; }
}
