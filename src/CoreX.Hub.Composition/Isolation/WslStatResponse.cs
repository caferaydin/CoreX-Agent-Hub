namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Reads a batched <c>stat</c> response, and tells two different silences apart.
///
/// <c>stat</c> reports a missing path on standard error and still lists the others, so "no row
/// came back for this path" normally means the path does not exist. It means exactly the same
/// thing when <c>wsl.exe</c> never reached the environment at all - a distribution that will not
/// start, a service that refused the call - and those two answers must not be confused. One is a
/// finding about the machine; the other is the absence of a measurement.
///
/// Asking about a path that cannot be missing settles it. If the control row is there, the
/// command ran inside the environment and every other absent row is a real absence. If it is
/// not, nothing was measured.
///
/// Pulled out as its own type because it is the difference between a readiness result that
/// reports what it saw and one that reports failures it invented, and that is worth being able
/// to test without a machine.
/// </summary>
public static class WslStatResponse
{
    /// <summary>
    /// A path that exists in every Linux environment, batched into each <c>stat</c> call.
    ///
    /// <c>/proc</c> is mounted before anything else a distribution does, and this file is
    /// synthesised by the kernel rather than shipped by a distribution, so no package choice,
    /// no minimal image and no cleanup can remove it.
    /// </summary>
    public const string ControlPath = "/proc/version";

    /// <summary>Whether the command ran inside the environment at all.</summary>
    public static bool Reached(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return Field(lines, ControlPath, index: 0) is not null;
    }

    /// <summary>
    /// Finds the row produced for one path and takes one field of it.
    ///
    /// Rows are formatted with a pipe separator rather than the usual colon, because a
    /// permission string and a filesystem type are printed beside a path and the path is the
    /// only field allowed to be ambiguous.
    /// </summary>
    public static string? Field(IEnumerable<string> lines, string path, int index)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        foreach (var line in lines)
        {
            var fields = line.Split('|');

            if (fields.Length > index && string.Equals(fields[0], path, StringComparison.Ordinal))
            {
                return fields[index];
            }
        }

        return null;
    }
}
