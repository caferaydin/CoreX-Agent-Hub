using System.Text.RegularExpressions;

namespace CoreX.Hub.Execution.Text;

/// <summary>
/// Removes terminal control sequences so captured output is readable evidence rather
/// than a screen recording. Raw output is preserved separately: normalisation must never
/// be the only copy (architecture F.5 #3).
/// </summary>
public static partial class AnsiNormalizer
{
    /// <summary>Strips CSI, OSC and single character escape sequences.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\u001b'))
        {
            return text ?? string.Empty;
        }

        var withoutOsc = OperatingSystemCommand().Replace(text, string.Empty);
        var withoutCsi = ControlSequence().Replace(withoutOsc, string.Empty);
        return SingleCharacterEscape().Replace(withoutCsi, string.Empty);
    }

    /// <summary>True when normalisation would change the text.</summary>
    public static bool ContainsEscapeSequences(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains('\u001b');

    // ESC [ parameters intermediates final-byte  (CSI)
    [GeneratedRegex("\u001b\\[[0-9;:?<>=!]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlSequence();

    // ESC ] ... terminated by BEL or by ESC backslash  (OSC)
    [GeneratedRegex("\u001b\\][^\u0007\u001b]*(\u0007|\u001b\\\\)", RegexOptions.CultureInvariant)]
    private static partial Regex OperatingSystemCommand();

    // Any remaining two character escape such as ESC 7 or ESC M, plus a lone ESC.
    [GeneratedRegex("\u001b[@-_]?", RegexOptions.CultureInvariant)]
    private static partial Regex SingleCharacterEscape();
}
