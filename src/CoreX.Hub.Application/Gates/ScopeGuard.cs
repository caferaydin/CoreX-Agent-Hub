using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Application.Gates;

/// <summary>
/// K2. Everything this execution touched has to be inside the scope the task declared.
///
/// Two rules, in order: a path must match the allowed scope, and it must match no
/// forbidden scope. Deny wins, so "src/**" allowed together with "src/generated/**"
/// forbidden means exactly what a reader expects.
///
/// Every matching decision belongs to the domain value object: what a scope entry means,
/// what it canonically is and what it matches are ScopeGlob's, so a task cannot be judged
/// by rules other than the ones it was validated against.
///
/// This is not a filesystem sandbox - FileSystemGuard is, and it already ran. This gate
/// answers one narrower question: was the change inside the source scope the task asked
/// for? A path it cannot understand is refused rather than assumed innocent.
/// </summary>
public static class ScopeGuard
{
    public const string ScopeViolation = "ScopeViolation";

    public static GateVerdict Evaluate(
        CapturedChanges changes,
        IReadOnlyList<string> allowedScope,
        IReadOnlyList<string> forbiddenScope)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(allowedScope);
        ArgumentNullException.ThrowIfNull(forbiddenScope);

        var allowed = Compile(allowedScope);
        var forbidden = Compile(forbiddenScope);
        var escapes = new List<ScopeEscape>();

        foreach (var path in changes.ChangedPaths)
        {
            if (Malformed(path) is { } malformed)
            {
                escapes.Add(new ScopeEscape(path, malformed));
                continue;
            }

            var normalised = ScopeGlob.NormalizePath(path);

            if (forbidden.FirstOrDefault(p => p.Matches(normalised)) is { } denied)
            {
                escapes.Add(new ScopeEscape(normalised, $"matches forbidden scope '{denied.Value}'"));
                continue;
            }

            if (!allowed.Any(p => p.Matches(normalised)))
            {
                escapes.Add(new ScopeEscape(normalised, "matches no allowed scope"));
            }
        }

        return escapes.Count == 0
            ? GateVerdict.Pass(GateKind.Scope)
            : GateVerdict.Block(
                GateKind.Scope,
                $"{ScopeViolation}: {escapes.Count} path(s) outside the declared scope") with
            {
                ScopeEscapes = escapes,
            };
    }

    /// <summary>
    /// Why a touched path cannot be judged at all. Fail closed: an absolute path, a drive
    /// qualified path, a UNC path or a traversal segment is not something to match
    /// leniently, it is something that should never have reached here.
    /// </summary>
    private static string? Malformed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "the path is empty";
        }

        var normalised = ScopeGlob.NormalizePath(path);

        if (normalised.Length == 0)
        {
            return "the path is empty once normalised";
        }

        var raw = path.Trim();

        // Checked on the raw text and before the rooted test, because normalisation
        // would otherwise turn a UNC path into an innocent looking relative one.
        if (raw.StartsWith("//", StringComparison.Ordinal)
            || raw.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return "the path looks like a UNC path";
        }

        if (raw.StartsWith('/') || raw.StartsWith('\\'))
        {
            return "the path is rooted rather than repository relative";
        }

        if (normalised.Length >= 2 && normalised[1] == ':')
        {
            return "the path is drive qualified";
        }

        var segments = normalised.Split('/');

        if (segments.Any(s => s == ".."))
        {
            return "the path traverses out of the repository";
        }

        return null;
    }

    private static IReadOnlyList<ScopeGlob> Compile(IReadOnlyList<string> globs) =>
        [.. globs.Where(g => !string.IsNullOrWhiteSpace(g)).Select(ScopeGlob.Parse)];
}
