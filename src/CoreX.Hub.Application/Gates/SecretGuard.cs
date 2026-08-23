using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Application.Gates;

/// <summary>
/// K3. Nothing this execution adds may contain a secret.
///
/// The scope is small on purpose: added diff lines only. Not the whole repository, not
/// untouched history, not deleted lines. A secret that was already committed is a
/// different conversation; this gate asks whether the Hub is about to commit a new one.
///
/// Detection comes from the one secret pattern source the system has, through a port, so
/// the gate and evidence redaction can never disagree about what a secret looks like.
/// There is no entropy scoring, no model, no vault and no network.
///
/// Verdicts carry the pattern family, the path and the line. They never carry the matched
/// text: a result object holding the secret would simply move the leak.
/// </summary>
public sealed class SecretGuard
{
    public const string SecretGuardBlocked = "SecretGuardBlocked";

    private readonly ISecretScanner _scanner;

    public SecretGuard(ISecretScanner scanner) =>
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));

    /// <summary>The families this gate can recognise, taken from the shared catalog.</summary>
    public IReadOnlyList<string> PatternNames => _scanner.PatternNames;

    public GateVerdict Evaluate(CapturedChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var hits = new List<SecretHit>();

        // Added lines are scanned per file as one block, because some shapes span lines -
        // a PEM private key block above all - and a scanner that looked at one line at a
        // time would miss those while appearing to work.
        var byFile = changes.AddedLines
            .GroupBy(l => ScopeGlob.NormalizePath(l.RelativePath), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var file in byFile)
        {
            var lines = file.OrderBy(l => l.LineNumber).ToList();
            var text = string.Join('\n', lines.Select(l => l.Text));

            foreach (var match in _scanner.Scan(text))
            {
                hits.Add(new SecretHit(match.PatternName, file.Key, LineAt(lines, text, match.CharacterOffset)));
            }
        }

        if (hits.Count == 0)
        {
            return GateVerdict.Pass(GateKind.Secret);
        }

        var families = hits
            .Select(h => h.PatternName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return GateVerdict.Block(
            GateKind.Secret,
            $"{SecretGuardBlocked}: {hits.Count} hit(s) in added lines [{string.Join(", ", families)}]") with
        {
            SecretHits = hits,
        };
    }

    /// <summary>
    /// Maps a character offset in the joined block back to the line number the diff
    /// reported, so a hit can be pointed at without the caller ever seeing the text.
    /// </summary>
    private static int? LineAt(IReadOnlyList<AddedLine> lines, string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
        {
            return null;
        }

        var consumed = 0;

        foreach (var line in lines)
        {
            // Each joined line costs its own length plus the single separator after it.
            var end = consumed + line.Text.Length;

            if (offset <= end)
            {
                return line.LineNumber;
            }

            consumed = end + 1;
        }

        return lines.Count > 0 ? lines[^1].LineNumber : null;
    }
}
