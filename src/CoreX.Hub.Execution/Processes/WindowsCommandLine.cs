using System.Text;

namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// Turns an argument vector into the single command line Windows actually accepts.
///
/// This exists because <c>CreateProcessW</c> has no argv parameter. It takes one string, and
/// the child pulls its arguments back out of that string with <c>CommandLineToArgvW</c> (or
/// the equivalent CRT startup code). So an argument vector has to survive a round trip
/// through a format that uses quotes and backslashes for its own purposes, and the encoding
/// has to be the exact inverse of that parser rather than something that merely looks close.
///
/// The rules are not symmetric or obvious, which is why they are implemented once here and
/// tested against a table rather than open-coded at the call site:
///
///   * a backslash is literal - unless a run of them is immediately followed by a quote, in
///     which case the run is halved and the quote is the delimiter;
///   * so a run of backslashes before a quote we want kept must be doubled, and one more
///     backslash added to escape the quote itself;
///   * a run of backslashes at the very end must be doubled too, because the closing quote
///     this encoder appends would otherwise consume them;
///   * an empty argument has no representation at all except a pair of quotes.
///
/// Nothing here is shell escaping. There is no shell (ADR-0006): this is the wire format of
/// one Win32 call, and a character that a shell would treat as an operator is just a
/// character to it.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>
    /// Builds the command line for an executable plus its arguments.
    ///
    /// The executable is repeated as the first token even though <c>CreateProcessW</c> is
    /// also given it as <c>lpApplicationName</c>: the child's own startup code reads argv[0]
    /// out of the command line, and a child whose argv[0] was its first real argument would
    /// see every argument shifted by one.
    /// </summary>
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();

        AppendArgument(builder, executable);

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument ?? string.Empty);
        }

        return builder.ToString();
    }

    /// <summary>Encodes one argument so <c>CommandLineToArgvW</c> gives it back unchanged.</summary>
    public static void AppendArgument(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(argument);

        // The common case: nothing in it can be mistaken for structure, so quoting it would
        // only add characters the parser would have to take back off.
        if (argument.Length > 0 && !NeedsQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        for (var i = 0; i < argument.Length;)
        {
            var current = argument[i++];

            if (current == '\\')
            {
                var backslashes = 1;

                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    // The closing quote is next. Double the run so the parser does not read
                    // these backslashes as an escape for it.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[i] == '"')
                {
                    // Double the run, then one more to escape the quote that follows it.
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    i++;
                }
                else
                {
                    // Ordinary backslashes in the middle of text: literal, left alone.
                    builder.Append('\\', backslashes);
                }
            }
            else if (current == '"')
            {
                builder.Append('\\');
                builder.Append('"');
            }
            else
            {
                builder.Append(current);
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Whether an argument would be split or reinterpreted if it were written bare.
    ///
    /// Space and tab separate arguments; a quote starts a quoted section. Newline and
    /// vertical tab are here because they are whitespace to some parsers and not to others -
    /// quoting them costs nothing and removes the question.
    /// </summary>
    private static bool NeedsQuoting(string argument)
    {
        foreach (var c in argument)
        {
            if (c is ' ' or '\t' or '\n' or '\v' or '"')
            {
                return true;
            }
        }

        return false;
    }
}
