using System.Text;
using System.Text.RegularExpressions;

namespace CoreX.Hub.Domain.Common;

/// <summary>
/// A validated repository relative scope expression (architecture D.4).
///
/// One authority for three things that must never disagree: what a scope entry may look
/// like, what it canonically is, and what it matches. A second matcher elsewhere would
/// let a task be judged by rules other than the ones it was validated against.
///
/// The language is deliberately tiny:
///
///   *   matches within a single path segment
///   ?   matches one character within a segment
///   **  matches across segments
///   a trailing / makes the entry a directory prefix
///
/// It is not a filesystem security component. FileSystemGuard is, and it enforces the
/// managed path boundary; this type means only "a validated repository relative scope
/// expression".
/// </summary>
public sealed class ScopeGlob : IEquatable<ScopeGlob>
{
    private readonly Regex _regex;

    private ScopeGlob(string value, Regex regex)
    {
        Value = value;
        _regex = regex;
    }

    /// <summary>The canonical text: one separator form, no "./", no repeated slashes.</summary>
    public string Value { get; }

    public static ScopeGlob Parse(string? value) =>
        TryParse(value, out var glob, out var error)
            ? glob!
            : throw new DomainException($"Scope glob '{value}' is invalid: {error}");

    public static bool TryParse(string? value, out ScopeGlob? glob, out string? error)
    {
        glob = null;
        error = Validate(value);

        if (error is not null)
        {
            return false;
        }

        var canonical = Canonicalize(value!);
        glob = new ScopeGlob(canonical, new Regex(Translate(canonical), RegexOptions.CultureInvariant));
        return true;
    }

    /// <summary>
    /// Why a pattern cannot be a repository relative scope, or null when it can.
    ///
    /// Fail closed: a pattern that reaches outside the repository is refused here rather
    /// than being interpreted charitably by whoever happens to match it later.
    /// </summary>
    public static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "the pattern is empty";
        }

        var raw = value.Trim();

        // Checked before canonicalisation, which would otherwise turn a UNC or rooted
        // pattern into an innocent looking relative one.
        if (raw.StartsWith("//", StringComparison.Ordinal) || raw.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "the pattern looks like a UNC path";
        }

        if (raw.StartsWith('/') || raw.StartsWith('\\'))
        {
            return "the pattern is rooted rather than repository relative";
        }

        var canonical = Canonicalize(raw);

        if (canonical.Length == 0)
        {
            return "the pattern is empty once canonicalised";
        }

        if (canonical.Length >= 2 && canonical[1] == ':')
        {
            return "the pattern is drive qualified";
        }

        return canonical.Split('/').Any(segment => segment == "..")
            ? "the pattern traverses out of the repository"
            : null;
    }

    /// <summary>
    /// The one canonical form for a repository relative path, used for scope entries and
    /// for the paths they are matched against.
    /// </summary>
    public static string NormalizePath(string? path)
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

    public bool Matches(string? repositoryRelativePath) =>
        _regex.IsMatch(NormalizePath(repositoryRelativePath));

    public bool Equals(ScopeGlob? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ScopeGlob);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    private static string Canonicalize(string value) => NormalizePath(value);

    private static string Translate(string glob)
    {
        // A directory prefix means everything under it.
        var pattern = glob.EndsWith('/') ? glob + "**" : glob;

        var builder = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (c == '*')
            {
                var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';

                if (isDoubleStar)
                {
                    i++;

                    // "a/**/b" must also match "a/b", so the separator is optional.
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
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
