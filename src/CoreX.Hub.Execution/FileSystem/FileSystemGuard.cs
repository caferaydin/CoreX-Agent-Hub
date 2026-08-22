namespace CoreX.Hub.Execution.FileSystem;

/// <summary>
/// The managed execution filesystem trust boundary (architecture E.1 #2, K.3 #7).
///
/// It answers exactly one question: is this path physically inside the trust root?
/// It is not a virtual filesystem, not a repository abstraction and not a wrapper around
/// File.* - it performs no I/O beyond the inspection needed to make that decision.
///
/// Two checks are combined, because either alone is insufficient:
///   * lexical canonicalisation, so <c>..</c> cannot climb out and a sibling whose name
///     merely starts with the root ("corex-evil" against root "corex") cannot slip in;
///   * physical resolution, so a symbolic link, junction or other reparse point cannot
///     point outside the root while still looking like a child.
/// </summary>
public sealed class FileSystemGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private FileSystemGuard(string trustRoot) => TrustRoot = trustRoot;

    /// <summary>Physically resolved, canonical, absolute trust root.</summary>
    public string TrustRoot { get; }

    /// <summary>
    /// Creates a guard for a trust root. The root must be absolute and must exist; a
    /// relative root is refused rather than silently resolved against the current
    /// working directory (architecture section 24 of the Phase 2 brief).
    ///
    /// A root that is itself a reparse point is resolved to its final target and that
    /// target becomes the boundary, so containment is always evaluated physically.
    /// </summary>
    public static PathValidationResult TryCreate(string? trustRoot, out FileSystemGuard? guard)
    {
        guard = null;

        if (string.IsNullOrWhiteSpace(trustRoot))
        {
            return PathValidationResult.Rejected(PathRejectionReason.MissingRoot, "Trust root is empty.");
        }

        var raw = trustRoot.Trim();

        if (IsUnsupportedShape(raw))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.UnsupportedPath,
                $"Trust root '{raw}' is a UNC or device path, which managed execution does not support.");
        }

        if (!Path.IsPathFullyQualified(raw))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.MissingRoot,
                $"Trust root '{raw}' is relative. A managed execution root must be absolute.");
        }

        string lexical;
        try
        {
            lexical = TrimTrailingSeparator(Path.GetFullPath(raw));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.InvalidPath,
                $"Trust root '{raw}' is not a valid path: {ex.Message}");
        }

        if (!Directory.Exists(lexical))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.MissingRoot,
                $"Trust root '{lexical}' does not exist.");
        }

        var resolved = TrimTrailingSeparator(ResolveFinalTarget(lexical) ?? lexical);

        if (!Directory.Exists(resolved))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.MissingRoot,
                $"Trust root '{lexical}' resolves to '{resolved}', which does not exist.");
        }

        guard = new FileSystemGuard(resolved);
        return PathValidationResult.Allowed(resolved);
    }

    /// <summary>Creates a guard or throws. Convenience for callers that cannot continue.</summary>
    public static FileSystemGuard Create(string trustRoot)
    {
        var result = TryCreate(trustRoot, out var guard);
        result.Require();
        return guard!;
    }

    /// <summary>Validates a path that must already exist.</summary>
    public PathValidationResult ValidateExisting(string? candidate) =>
        Validate(candidate, PathIntent.ExistingPath);

    /// <summary>
    /// Validates a path a later phase intends to create. Every existing ancestor is still
    /// checked for reparse escape; only the missing tail is tolerated.
    /// </summary>
    public PathValidationResult ValidateProposed(string? candidate) =>
        Validate(candidate, PathIntent.ProposedPath);

    public PathValidationResult Validate(string? candidate, PathIntent intent)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return PathValidationResult.Rejected(PathRejectionReason.InvalidPath, "Path is empty.");
        }

        var raw = candidate.Trim();

        if (IsUnsupportedShape(raw))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.UnsupportedPath,
                $"'{raw}' is a UNC or device path, which managed execution does not support.");
        }

        string lexical;
        try
        {
            // A relative candidate is resolved against the trust root, never against the
            // process working directory.
            lexical = TrimTrailingSeparator(
                Path.IsPathFullyQualified(raw)
                    ? Path.GetFullPath(raw)
                    : Path.GetFullPath(Path.Combine(TrustRoot, raw)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.InvalidPath,
                $"'{raw}' is not a valid path: {ex.Message}");
        }

        if (!IsWithin(TrustRoot, lexical))
        {
            var reason = HasParentSegment(raw)
                ? PathRejectionReason.TraversalEscape
                : PathRejectionReason.OutsideRoot;

            return PathValidationResult.Rejected(
                reason,
                $"'{lexical}' is outside the trust root '{TrustRoot}'.");
        }

        var physical = ResolvePhysically(lexical, out var escapeDetail);
        if (physical is null)
        {
            return PathValidationResult.Rejected(PathRejectionReason.ReparsePointEscape, escapeDetail!);
        }

        if (!IsWithin(TrustRoot, physical))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.ReparsePointEscape,
                $"'{lexical}' physically resolves to '{physical}', outside the trust root '{TrustRoot}'.");
        }

        if (intent == PathIntent.ExistingPath && !Exists(physical))
        {
            return PathValidationResult.Rejected(
                PathRejectionReason.MissingRequiredPath,
                $"'{physical}' does not exist.");
        }

        return PathValidationResult.Allowed(physical);
    }

    /// <summary>
    /// Walks the candidate segment by segment from the trust root, following any reparse
    /// point it meets. Returns the physically resolved path, or null when a reparse point
    /// leaves the root.
    /// </summary>
    private string? ResolvePhysically(string lexicalPath, out string? escapeDetail)
    {
        escapeDetail = null;

        var relative = Path.GetRelativePath(TrustRoot, lexicalPath);
        if (relative is "." or "")
        {
            return TrustRoot;
        }

        var current = TrustRoot;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            if (!Exists(current))
            {
                // The tail does not exist yet, so it cannot be a reparse point. Everything
                // that did exist has already been checked.
                var remainder = lexicalPath[Math.Min(current.Length, lexicalPath.Length)..];
                return TrimTrailingSeparator(current + remainder);
            }

            if (!IsReparsePoint(current))
            {
                continue;
            }

            var target = ResolveFinalTarget(current);
            if (target is null)
            {
                escapeDetail = $"'{current}' is a reparse point whose target could not be resolved.";
                return null;
            }

            target = TrimTrailingSeparator(target);

            if (!IsWithin(TrustRoot, target))
            {
                escapeDetail =
                    $"'{current}' is a reparse point targeting '{target}', outside the trust root '{TrustRoot}'.";
                return null;
            }

            current = target;
        }

        return TrimTrailingSeparator(current);
    }

    /// <summary>
    /// Segment aware containment. A plain StartsWith would wrongly accept
    /// <c>D:\cx\corex-evil</c> for the root <c>D:\cx\corex</c>.
    /// </summary>
    private static bool IsWithin(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool HasParentSegment(string raw) =>
        raw.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.None)
            .Any(s => s == "..");

    private static bool IsUnsupportedShape(string raw) =>
        raw.StartsWith(@"\\", StringComparison.Ordinal)
        || raw.StartsWith("//", StringComparison.Ordinal);

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat an unreadable entry as suspicious rather than safe.
            return true;
        }
    }

    /// <summary>
    /// Resolves symbolic links, junctions and other reparse points to their final target.
    /// Returns the input when it is not a link, and null when resolution fails.
    /// </summary>
    private static string? ResolveFinalTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        // Keep the separator for a drive root such as "D:\".
        if (path.Length == 3 && path[1] == ':' && IsSeparator(path[2]))
        {
            return path;
        }

        return IsSeparator(path[^1]) ? path[..^1] : path;
    }

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
