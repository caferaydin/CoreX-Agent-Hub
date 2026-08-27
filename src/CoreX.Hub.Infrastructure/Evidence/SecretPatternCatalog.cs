using System.Text.RegularExpressions;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>
/// One bounded secret shape and the marker that replaces it.
///
/// The expression is text rather than a built <see cref="Regex"/> because how long it may run
/// depends on the artifact being examined, and the platform fixes a timeout when the expression is
/// built. Keeping the text here and building through <see cref="Compile"/> is what lets one
/// catalogue serve a small artifact and a large one without either a second catalogue or an
/// unbounded budget.
/// </summary>
public sealed record SecretPattern(
    string Name,
    string Expression,
    RegexOptions Options,
    string Replacement)
{
    /// <summary>The label written into redacted evidence.</summary>
    public string Marker => "[REDACTED:" + Name + "]";

    /// <summary>
    /// Builds this shape for one operation.
    ///
    /// The options are exactly what the shape needs and nothing else. In particular the linear
    /// engine is not among them: on this runtime it was measured to return no matches at all -
    /// silently, without throwing - for several of these shapes once the input passed about a
    /// mebibyte and a finite timeout was set. A secret in a large artifact would have gone
    /// unredacted and unreported, so the faster engine is not available at any price here.
    /// </summary>
    public Regex Compile(TimeSpan timeout) => new(Expression, Options, timeout);
}

/// <summary>
/// The single source of truth for the MVP secret shapes.
///
/// The frozen architecture requires evidence redaction and the later SecretGuard to agree
/// on what a secret looks like. Duplicating these expressions in Phase 5 would let the two
/// drift, so the catalog lives here once and a future gate consumes it through
/// composition (Phase 4 brief section 31).
///
/// Deliberately not implemented: entropy scoring, machine learning, vaults, rotation and
/// secret managers. These are bounded, readable, testable shapes and nothing more.
///
/// Known limitations, accepted as best effort:
///   - a secret split across two writes cannot be matched;
///   - a token shape shorter than the minimum length is not matched;
///   - a bespoke internal credential format nobody declared is not matched;
///   - a base64 blob that happens to look like a JWT is redacted even if it is not one;
///   - the PEM shape can be made to backtrack super-linearly by input full of unterminated
///     headers, which is bounded by the budget rather than by the expression.
/// </summary>
public static class SecretPatternCatalog
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// Ordered widest first: a PEM block is replaced as a whole before anything inside it
    /// can be matched piecemeal.
    /// </summary>
    public static IReadOnlyList<SecretPattern> Patterns { get; } =
    [
        new("PRIVATE_KEY",
            @"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\s\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
            Options,
            "[REDACTED:PRIVATE_KEY]"),

        new("AUTHORIZATION",
            @"(?<=\bAuthorization[ \t]*:[ \t]*)[^\r\n]+",
            Options | RegexOptions.IgnoreCase,
            "[REDACTED:AUTHORIZATION]"),

        new("PASSWORD",
            @"(?<=\b(?:password|pwd)[ \t]*=[ \t]*)[^;\r\n""']+",
            Options | RegexOptions.IgnoreCase,
            "[REDACTED:PASSWORD]"),

        new("JWT",
            @"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}",
            Options,
            "[REDACTED:JWT]"),

        new("OPENAI_TOKEN",
            @"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}",
            Options,
            "[REDACTED:OPENAI_TOKEN]"),

        new("GITHUB_TOKEN",
            @"\bgh[pousr]_[A-Za-z0-9]{20,}",
            Options,
            "[REDACTED:GITHUB_TOKEN]"),

        new("AWS_ACCESS_KEY",
            @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
            Options,
            "[REDACTED:AWS_ACCESS_KEY]"),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. Patterns.Select(p => p.Name)];
}
