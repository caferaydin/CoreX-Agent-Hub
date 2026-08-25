using System.Globalization;
using System.Text.RegularExpressions;
using CoreX.Hub.Application.Gates;

namespace CoreX.Hub.Composition.Gates;

/// <summary>
/// Turns the Phase 3 workspace capture into the bounded machine facts the gates evaluate.
///
/// Composition work, and deliberately the only place git's diff text is interpreted. The
/// gates never see git output: they see paths and added lines. Nothing here runs git,
/// touches the filesystem or writes evidence - the capture already happened.
/// </summary>
public static partial class CapturedChangesMapper
{
    public static CapturedChanges FromCapture(
        string baseCommitSha,
        string observedHeadSha,
        IReadOnlyList<string> unexpectedCommitShas,
        IReadOnlyList<string> changedPaths,
        string worktreeDiff) => new()
    {
        BaseCommitSha = baseCommitSha,
        ObservedHeadSha = observedHeadSha,
        UnexpectedCommitShas = unexpectedCommitShas,
        ChangedPaths = changedPaths,
        AddedLines = ReadAddedLines(worktreeDiff),
    };

    /// <summary>
    /// Extracts added lines from a unified diff, with the destination path and the line
    /// number in the new file.
    ///
    /// Only added content: context lines and removals are not what this execution is
    /// introducing. The "+++" header and the "+++"-like marker inside a hunk are
    /// distinguished by position, which is why the header is handled before the body.
    /// </summary>
    public static IReadOnlyList<AddedLine> ReadAddedLines(string diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return [];
        }

        var added = new List<AddedLine>();
        var path = string.Empty;
        var lineNumber = 0;

        foreach (var raw in diff.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.StartsWith("+++ ", StringComparison.Ordinal))
            {
                path = ReadDestinationPath(raw);
                continue;
            }

            if (raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                lineNumber = ReadHunkStart(raw);
                continue;
            }

            if (raw.StartsWith('+') && path.Length > 0)
            {
                added.Add(new AddedLine(path, lineNumber, raw[1..]));
                lineNumber++;
                continue;
            }

            if (raw.StartsWith('-') || raw.StartsWith('\\'))
            {
                // A removal or a "no newline" marker occupies no line in the new file.
                continue;
            }

            if (lineNumber > 0)
            {
                lineNumber++;
            }
        }

        return added;
    }

    private static string ReadDestinationPath(string header)
    {
        var value = header[4..].Trim();

        if (string.Equals(value, "/dev/null", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return value.StartsWith("b/", StringComparison.Ordinal) ? value[2..] : value;
    }

    private static int ReadHunkStart(string header)
    {
        var match = HunkHeader().Match(header);

        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? start
            : 0;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
