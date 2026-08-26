using System.Text;

namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// Builds the environment block <c>CreateProcessW</c> expects, from the names the request
/// actually allowed.
///
/// The block is one buffer, not a list: <c>NAME=VALUE\0NAME=VALUE\0\0</c>. Two details are
/// easy to get wrong and are both load-bearing here. It must end with an extra NUL, so an
/// empty environment is still two NULs rather than nothing at all - and an empty environment
/// is the normal case for this Hub, because the child starts from nothing (ADR-0007). And it
/// must be sorted case-insensitively, which is what Windows itself does when it hands a block
/// to a child; an unsorted block is accepted but leaves the child's own lookups inconsistent
/// with every other process on the machine.
///
/// Names are compared case-insensitively because that is what Windows environment semantics
/// are - <c>Path</c> and <c>PATH</c> are one variable, and a block containing both would be
/// a block whose meaning depends on which one the child's parser reached first.
/// </summary>
internal static class WindowsEnvironmentBlock
{
    /// <summary>
    /// Collapses the caller's layered values into the final set the child will see.
    ///
    /// Later layers win, which is the precedence the request contract already states: the
    /// allowlist copies what was asked for, explicit values override those, and the Hub's own
    /// forced values override everything, so a caller cannot re-enable something the Hub
    /// disabled by naming it twice.
    /// </summary>
    public static Dictionary<string, string> Compose(
        IReadOnlyList<string> allowlist,
        Func<string, string?> read,
        IReadOnlyDictionary<string, string> additional,
        IReadOnlyDictionary<string, string> forced)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(additional);
        ArgumentNullException.ThrowIfNull(forced);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in allowlist)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Absent in the parent means absent in the child. Naming a variable that does not
            // exist is not an error and does not invent an empty one.
            if (read(name) is { } value)
            {
                environment[name] = value;
            }
        }

        foreach (var entry in additional)
        {
            environment[entry.Key] = entry.Value;
        }

        foreach (var entry in forced)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    /// <summary>
    /// Renders the block as UTF-16 characters, terminated the way Windows requires.
    ///
    /// Returned as a char array rather than a string because the caller pins it and hands the
    /// address to the kernel; a string would carry its own terminator and hide the second NUL
    /// this format depends on.
    /// </summary>
    public static char[] Render(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var builder = new StringBuilder();

        foreach (var entry in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Key);
            builder.Append('=');
            builder.Append(entry.Value);
            builder.Append('\0');
        }

        // The terminator for the block itself. Present even when nothing precedes it.
        builder.Append('\0');

        var block = new char[builder.Length];
        builder.CopyTo(0, block, 0, builder.Length);

        return block;
    }
}
