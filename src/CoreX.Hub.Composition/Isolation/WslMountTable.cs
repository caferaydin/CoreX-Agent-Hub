namespace CoreX.Hub.Composition.Isolation;

/// <summary>One mounted filesystem, reduced to the parts a readiness check may look at.</summary>
/// <param name="MountPoint">Where it appears inside the environment. Safe to print.</param>
/// <param name="FilesystemType">What kind of filesystem it is. Safe to print.</param>
/// <param name="HostDriveLetter">The host volume it exposes, when it exposes one.</param>
internal sealed record MountEntry(string MountPoint, string FilesystemType, char? HostDriveLetter);

/// <summary>
/// The environment's mount table, parsed into the few facts that decide reachability.
///
/// Deliberately lossy. <c>/proc/self/mountinfo</c> carries mount sources and super options,
/// which for a mounted host volume include real host paths; none of that is kept, because a
/// readiness result is written to diagnostics and evidence, and a check can be answered
/// perfectly well from a mount point and a filesystem type.
/// </summary>
internal static class WslMountTable
{
    /// <summary>
    /// Filesystems that are a window onto something outside the environment.
    ///
    /// <c>drvfs</c> and <c>9p</c> are how WSL exposes Windows volumes; the network ones are
    /// here because a hand mounted share is the same hole arrived at differently.
    /// </summary>
    private static readonly HashSet<string> ForeignFilesystems =
        new(StringComparer.OrdinalIgnoreCase) { "drvfs", "9p", "v9fs", "cifs", "smbfs", "smb3", "nfs", "nfs4" };

    /// <summary>
    /// Filesystems a Linux environment can genuinely own.
    ///
    /// <c>stat -f</c> reports the ext family as "ext2/ext3" whichever of them it is, which is
    /// why that spelling appears literally.
    /// </summary>
    public static IReadOnlySet<string> NativeFilesystems { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ext2/ext3", "ext2", "ext3", "ext4", "xfs", "btrfs", "overlayfs", "overlay",
        };

    /// <summary>
    /// Mount points shared between every environment on the same WSL virtual machine.
    ///
    /// These are not host volumes and nothing about them is a Windows path, which is exactly
    /// what makes them easy to overlook: they are a tmpfs that two distributions can both
    /// write to, so one hard environment can hand something to another through them.
    /// </summary>
    public static IReadOnlyList<string> CrossEnvironmentMountPoints { get; } = ["/mnt/wsl", "/mnt/wslg"];

    /// <summary>
    /// Reads the entries this probe cares about out of a mountinfo dump.
    ///
    /// The format is positional up to a variable length optional field list, which ends at a
    /// lone "-". Everything before it is ignored here except the mount point; the separator is
    /// followed by the filesystem type, and that is all this needs.
    /// </summary>
    public static IReadOnlyList<MountEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<MountEntry>();

        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var separator = Array.IndexOf(fields, "-");

            // Fields 0-4 are ids, parent, device, root and mount point; the type follows the
            // separator. A line shaped otherwise is not mountinfo and is not guessed at.
            if (separator < 5 || separator + 1 >= fields.Length)
            {
                continue;
            }

            var mountPoint = Unescape(fields[4]);
            var type = fields[separator + 1];

            entries.Add(new MountEntry(mountPoint, type, DriveLetterOf(mountPoint, type)));
        }

        return entries;
    }

    public static bool IsForeign(MountEntry entry) => ForeignFilesystems.Contains(entry.FilesystemType);

    /// <summary>
    /// Which host volume a mount exposes, when that can be said from the mount point alone.
    ///
    /// Only the mount point is consulted. The mount source would be more direct and is not
    /// used, because it is a host path and this type exists not to carry those.
    /// </summary>
    private static char? DriveLetterOf(string mountPoint, string type)
    {
        if (!ForeignFilesystems.Contains(type))
        {
            return null;
        }

        var lastSeparator = mountPoint.LastIndexOf('/');
        var leaf = lastSeparator >= 0 ? mountPoint[(lastSeparator + 1)..] : mountPoint;

        return leaf.Length == 1 && char.IsAsciiLetter(leaf[0])
            ? char.ToUpperInvariant(leaf[0])
            : null;
    }

    /// <summary>
    /// Turns mountinfo's octal escapes back into characters.
    ///
    /// Space, tab, newline and backslash are escaped there because the format is space
    /// separated. A mount point containing one is unusual and is still a mount point.
    /// </summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length && IsOctalTriple(value, i + 1))
            {
                builder.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool IsOctalTriple(string value, int start) =>
        value[start] is >= '0' and <= '7'
        && value[start + 1] is >= '0' and <= '7'
        && value[start + 2] is >= '0' and <= '7';
}
