using System.Text.RegularExpressions;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>One bounded secret shape and the marker that replaces it.</summary>
public sealed record SecretPattern(string Name, Regex Pattern, string Replacement)
{
    /// <summary>The label written into redacted evidence.</summary>
    public string Marker => "[REDACTED:" + Name + "]";
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
///   - a base64 blob that happens to look like a JWT is redacted even if it is not one.
/// </summary>
public static class SecretPatternCatalog
{
    /// <summary>
    /// What stops a pattern running away, and deliberately not a cap on how much evidence may be
    /// redacted.
    ///
    /// The two failures look alike from here and are not. Catastrophic backtracking is
    /// super-linear and blows through any budget on a small input, which is what this exists to
    /// catch. Scanning a large artifact is linear and slow only in proportion to its size - and an
    /// artifact's size is the producer's business, bounded elsewhere.
    ///
    /// Two seconds could not tell them apart: it was chosen against ordinary command output and
    /// made a sixteen megabyte test report throw, which is an artifact the evidence contract
    /// accepts and would therefore have refused to store on a machine that was merely busy.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(60);

    private static readonly RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>
    /// Ordered widest first: a PEM block is replaced as a whole before anything inside it
    /// can be matched piecemeal.
    /// </summary>
    public static IReadOnlyList<SecretPattern> Patterns { get; } =
    [
        new("PRIVATE_KEY",
            new Regex(
                @"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\s\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
                Options, MatchTimeout),
            "[REDACTED:PRIVATE_KEY]"),

        new("AUTHORIZATION",
            new Regex(
                @"(?<=\bAuthorization[ \t]*:[ \t]*)[^\r\n]+",
                Options | RegexOptions.IgnoreCase, MatchTimeout),
            "[REDACTED:AUTHORIZATION]"),

        new("PASSWORD",
            new Regex(
                @"(?<=\b(?:password|pwd)[ \t]*=[ \t]*)[^;\r\n""']+",
                Options | RegexOptions.IgnoreCase, MatchTimeout),
            "[REDACTED:PASSWORD]"),

        new("JWT",
            new Regex(
                @"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}",
                Options, MatchTimeout),
            "[REDACTED:JWT]"),

        new("OPENAI_TOKEN",
            new Regex(
                @"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}",
                Options, MatchTimeout),
            "[REDACTED:OPENAI_TOKEN]"),

        new("GITHUB_TOKEN",
            new Regex(
                @"\bgh[pousr]_[A-Za-z0-9]{20,}",
                Options, MatchTimeout),
            "[REDACTED:GITHUB_TOKEN]"),

        new("AWS_ACCESS_KEY",
            new Regex(
                @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
                Options, MatchTimeout),
            "[REDACTED:AWS_ACCESS_KEY]"),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. Patterns.Select(p => p.Name)];
}
