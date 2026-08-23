namespace CoreX.Hub.Application.Gates;

/// <summary>
/// What a scanner found, without what it found. The offset is a character index into the
/// text that was scanned, so the caller can name a line without the scanner knowing what
/// a line is.
/// </summary>
public sealed record SecretScanMatch(string PatternName, int CharacterOffset);

/// <summary>
/// The Application owned port for secret detection.
///
/// There is exactly one secret pattern source in the system, and evidence redaction
/// already owns it. This port exists so the gate consumes that same source rather than
/// growing a second catalog that could drift from it - a gate and a redactor that
/// disagree about what a secret looks like is the worst of both.
///
/// It scans a block rather than a line because some shapes span lines: a PEM private key
/// block is the obvious one, and a line by line scanner would miss it entirely while
/// appearing to work.
///
/// It returns metadata only. A scanner that handed back the matched text would make every
/// caller a place a secret could leak from.
/// </summary>
public interface ISecretScanner
{
    /// <summary>Names of the pattern families this scanner knows, for diagnostics and guards.</summary>
    IReadOnlyList<string> PatternNames { get; }

    /// <summary>Finds pattern hits in a block of text. Never returns the matched value.</summary>
    IReadOnlyList<SecretScanMatch> Scan(string text);
}
