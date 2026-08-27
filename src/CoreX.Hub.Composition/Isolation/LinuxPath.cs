namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Whether a Linux path is one a hard isolated run may use.
///
/// Segment aware rather than textual, because the interesting failures are all textual tricks:
/// <c>/srv/corex/exec/../../etc</c> reads as if it were under the execution root and is not, and
/// a prefix comparison would agree with it. So the path is decomposed and judged segment by
/// segment, and containment is decided on the decomposed form.
///
/// It also refuses the shapes that are not Linux paths at all - a Windows drive, a backslash, a
/// path under a mounted host volume. Those cannot be right here by construction: a hard
/// environment stores nothing outside itself, and a Linux name for a Windows directory is
/// neither contained nor governed by anything in this design.
///
/// What it deliberately does not do is touch the filesystem. Whether the directory exists, and
/// whether it is a directory at all, is a question for the environment, and the executor asks it
/// there.
/// </summary>
public static class LinuxPath
{
    /// <summary>Mount points under which a path is a host directory wearing a Linux name.</summary>
    private static readonly string[] HostMountRoots = ["/mnt", "/media", "/proc", "/sys", "/dev"];

    /// <summary>
    /// Reduces a path to its meaningful segments, or explains why it is not usable.
    ///
    /// <c>.</c> is dropped and <c>..</c> is refused outright rather than resolved. Resolving it
    /// would be defensible for a path the Hub built itself, and this validates paths the Hub was
    /// handed - where a <c>..</c> is either a mistake or an attempt, and neither deserves a
    /// helpful interpretation.
    /// </summary>
    public static bool TryNormalize(string? path, out IReadOnlyList<string> segments, out string? problem)
    {
        segments = [];
        problem = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "the path is empty.";
            return false;
        }

        var value = path.Trim();

        if (!value.StartsWith('/'))
        {
            problem = $"'{value}' is not an absolute Linux path.";
            return false;
        }

        if (value.Contains('\\', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal))
        {
            problem = $"'{value}' looks like a Windows path; a hard environment stores nothing on the host.";
            return false;
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            problem = "the path contains a null character.";
            return false;
        }

        var parts = new List<string>();

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                problem = $"'{value}' walks upwards; a working directory is stated, not navigated to.";
                return false;
            }

            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            problem = "the path names the filesystem root rather than a directory.";
            return false;
        }

        if (HostMountRoots.Contains("/" + parts[0], StringComparer.Ordinal))
        {
            problem = $"'{value}' is under '/{parts[0]}', which is not the environment's own storage.";
            return false;
        }

        segments = parts;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the same as, or beneath, <paramref name="root"/>.
    ///
    /// Both sides are normalized first, so this is a segment comparison and never a string
    /// prefix one: <c>/srv/corex/execution</c> is not under <c>/srv/corex/exec</c>, however
    /// alike they look.
    /// </summary>
    public static bool TryContain(string? root, string? candidate, out string? problem)
    {
        if (!TryNormalize(root, out var rootSegments, out problem))
        {
            problem = $"the containing root is unusable: {problem}";
            return false;
        }

        if (!TryNormalize(candidate, out var candidateSegments, out problem))
        {
            return false;
        }

        if (candidateSegments.Count < rootSegments.Count
            || !rootSegments.SequenceEqual(candidateSegments.Take(rootSegments.Count), StringComparer.Ordinal))
        {
            problem =
                $"'{Join(candidateSegments)}' is not inside '{Join(rootSegments)}'; a hard isolated run works "
                + "only beneath its own execution root.";

            return false;
        }

        return true;
    }

    /// <summary>Renders normalized segments back into a path.</summary>
    public static string Join(IReadOnlyList<string> segments) => "/" + string.Join('/', segments);
}
