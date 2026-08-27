using CoreX.Hub.Application.Gates;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>
/// The gate's view of the one secret pattern catalog.
///
/// This is the whole point of the port: SecretGuard and evidence redaction read the same
/// <see cref="SecretPatternCatalog"/>, so a shape that gets redacted out of evidence is
/// also a shape that blocks a commit, and neither can quietly drift from the other. There
/// is no second regex set anywhere, and an architecture guard keeps it that way.
///
/// It lives here rather than in the composition layer because infrastructure already
/// depends on application: implementing an application port beside the catalog it wraps
/// adds no dependency at all, where an adapter elsewhere would have needed one.
///
/// It returns the family and an offset and nothing else. Handing the matched text back
/// would turn every caller into a place a secret could escape from.
/// </summary>
public sealed class CatalogSecretScanner : ISecretScanner
{
    private readonly IReadOnlyList<SecretPattern> _patterns;

    public CatalogSecretScanner()
        : this(SecretPatternCatalog.Patterns)
    {
    }

    public CatalogSecretScanner(IReadOnlyList<SecretPattern> patterns)
    {
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        PatternNames = [.. _patterns.Select(p => p.Name)];
    }

    public IReadOnlyList<string> PatternNames { get; }

    public IReadOnlyList<SecretScanMatch> Scan(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        // The same budget authority evidence redaction uses, for the same reason the catalogue is
        // shared: a gate and a redactor that disagreed about how long a scan may take would
        // disagree about which diffs get examined. Running out throws rather than returning
        // nothing - "I did not finish" and "there is nothing here" are opposite answers, and a gate
        // that confused them would pass exactly the change it exists to stop.
        var budget = SecretBudget.Open(text.Length);
        var matches = new List<SecretScanMatch>();

        foreach (var pattern in _patterns)
        {
            foreach (System.Text.RegularExpressions.Match match in budget.Next(pattern).Matches(text))
            {
                matches.Add(new SecretScanMatch(pattern.Name, match.Index));
            }
        }

        return [.. matches.OrderBy(m => m.CharacterOffset).ThenBy(m => m.PatternName, StringComparer.Ordinal)];
    }
}
