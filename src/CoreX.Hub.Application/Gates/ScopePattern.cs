using System.Text;
using System.Text.RegularExpressions;

namespace CoreX.Hub.Application.Gates;

/// <summary>
/// One scope glob from the task, compiled once.
///
/// The task model stores scope as plain strings and nothing in the system interpreted
/// them before now, so this is where the meaning is fixed - narrowly, and in one place:
///
///   *   matches within a single path segment
///   ?   matches one character within a segment
///   **  matches across segments
///   a trailing / or /** makes the entry a directory prefix
///
/// That is the whole language. It is not a policy expression engine and never becomes one.
/// </summary>
public sealed partial class ScopePattern
{
    private readonly Regex _regex;

    private ScopePattern(string original, Regex regex)
    {
        Original = original;
        _regex = regex;
    }

    public string Original { get; }

    public static ScopePattern Parse(string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);

        var normalised = Normalize(glob);

        // "src/" and "src" both mean "everything under src".
        if (normalised.EndsWith('/'))
        {
            normalised += "**";
        }

        return new ScopePattern(glob.Trim(), new Regex(Translate(normalised), RegexOptions.CultureInvariant));
    }

    public bool Matches(string repositoryRelativePath) =>
        _regex.IsMatch(Normalize(repositoryRelativePath));

    /// <summary>One separator form, no leading "./", no repeated slashes.</summary>
    public static string Normalize(string path)
    {
        var text = (path ?? string.Empty).Trim().Replace('\\', '/');

        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        while (text.Contains("//", StringComparison.Ordinal))
        {
            text = text.Replace("//", "/", StringComparison.Ordinal);
        }

        return text.TrimStart('/');
    }

    private static string Translate(string glob)
    {
        var builder = new StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            if (c == '*')
            {
                var isDoubleStar = i + 1 < glob.Length && glob[i + 1] == '*';

                if (isDoubleStar)
                {
                    i++;

                    // "a/**/b" must also match "a/b", so the separator is optional.
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            builder.Append(c switch
            {
                '?' => "[^/]",
                '/' => "/",
                _ => Regex.Escape(c.ToString()),
            });
        }

        return builder.Append('$').ToString();
    }
}
