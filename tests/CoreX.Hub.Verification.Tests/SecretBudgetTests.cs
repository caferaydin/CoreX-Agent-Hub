using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// How long the secret catalogue may look at an artifact, and what it may conclude when it runs out.
///
/// The defect these exist for was subtle in exactly the wrong way. A flat two second budget cannot
/// tell a large legitimate artifact - slow in proportion to its size - from a small hostile one that
/// is slow because a pattern is backtracking, so it either refuses evidence the contract accepts or
/// hands a hostile input a generous share of a machine. Every case below keeps those two apart, and
/// insists that running out of time is a refusal rather than a shorter list of patterns.
///
/// Every value here is synthetic. Nothing in this file is a real credential.
/// </summary>
public sealed class SecretBudgetTests : IDisposable
{
    private const int Mebibyte = 1024 * 1024;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "corex-budget-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Fake(string family) => family switch
    {
        "JWT" => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXkQ",
        "OPENAI_TOKEN" => "sk-proj-AAAAAAAAAAAAAAAAAAAABBBBBBBBBBBBBBBBBBBB",
        "GITHUB_TOKEN" => "ghp_AAAAAAAAAAAAAAAAAAAABBBBBBBBBB",
        "AWS_ACCESS_KEY" => "AKIAAAAAAAAAAAAAAAAA",
        "AUTHORIZATION" => "Authorization: Bearer aaaaaaaaaaaaaaaaaaaaaaaa",
        "PASSWORD" => "password=NotARealSecretValue1",
        "PRIVATE_KEY" => "-----BEGIN RSA PRIVATE KEY-----\nAAAABBBBCCCC\n-----END RSA PRIVATE KEY-----",
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };

    /// <summary>
    /// The corpus every parity claim is judged against.
    ///
    /// Positive examples, near misses, boundaries, mixed case, adjacency, several families in one
    /// text, a long ordinary prefix and non-ASCII around a match - the cases where two
    /// configurations that agreed on simple inputs could still disagree.
    /// </summary>
    private static IReadOnlyList<string> Corpus { get; } =
    [
        string.Empty,
        "nothing interesting here",
        Fake("JWT"),
        "prefix " + Fake("JWT") + " suffix",
        Fake("JWT") + " " + Fake("JWT"),
        Fake("JWT") + Fake("OPENAI_TOKEN"),
        Fake("OPENAI_TOKEN"),
        "sk-short",
        "sk-proj-tooshort",
        Fake("GITHUB_TOKEN"),
        "ghx_AAAAAAAAAAAAAAAAAAAABBBBBBBBBB",
        Fake("AWS_ACCESS_KEY"),
        "AKIA0000",
        "AKIAAAAAAAAAAAAAAAAAX",
        Fake("AUTHORIZATION"),
        "authorization:   Bearer zzzz",
        "AUTHORIZATION:\tBearer TAB",
        "Authorization:",
        "not an Authorization line",
        Fake("PASSWORD"),
        "PWD=abc;next=1",
        "PWD = spaced;",
        "password = spaced value",
        "password=" + new string('v', 4000),
        "line1\n" + Fake("AUTHORIZATION") + "\nline3\n" + Fake("PASSWORD"),
        Fake("PRIVATE_KEY"),
        "-----BEGIN PRIVATE KEY-----\nzzz\n-----END PRIVATE KEY-----",
        "-----BEGIN OPENSSH PRIVATE KEY-----\nzz\n-----END OPENSSH PRIVATE KEY-----",
        "-----BEGIN RSA PRIVATE KEY-----\nno end marker here",
        "ünïcode " + Fake("JWT") + " ünïcode",
        new string('x', 20_000) + " " + Fake("JWT") + " " + new string('y', 20_000),
        string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i} " + (i % 7 == 0 ? Fake("GITHUB_TOKEN") : "clean"))),
    ];

    /// <summary>Ordinary log-shaped text with no secret in it, at a requested size.</summary>
    private static string Filler(int chars)
    {
        var builder = new StringBuilder(chars + 160);
        var line = 0;

        while (builder.Length < chars)
        {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"2026-08-27T21:{line % 60:D2}:{(line * 7) % 60:D2} INFO worker[{line % 16}] batch {line} took {(line * 13) % 900}ms items={(line * 3) % 500}\n");
            line++;
        }

        return builder.ToString(0, chars);
    }

    // -- the budget itself -----------------------------------------------------------------------

    /// <summary>
    /// A small input gets a small budget and a large one gets more, which is the whole correction.
    ///
    /// A flat budget is what let a hostile quarter mebibyte have the same share of a machine as a
    /// sixteen mebibyte artifact. Scaling with size is what separates them.
    /// </summary>
    [Fact]
    public void A_small_input_gets_a_smaller_budget_than_a_large_one()
    {
        Assert.True(SecretBudget.PerPattern(256 * 1024) < SecretBudget.PerPattern(16 * Mebibyte));
        Assert.True(SecretBudget.PerPattern(1024) < SecretBudget.PerPattern(Mebibyte));
        Assert.True(SecretBudget.Operation(256 * 1024) < SecretBudget.Operation(16 * Mebibyte));
    }

    /// <summary>The calibrated shape, at the sizes it was calibrated against.</summary>
    [Theory]
    [InlineData(256 * 1024, 575)]
    [InlineData(4 * Mebibyte, 1700)]
    [InlineData(16 * Mebibyte, 5300)]
    [InlineData(64 * Mebibyte, 8000)]
    public void The_budget_matches_what_was_calibrated(int length, int expectedMilliseconds) =>
        Assert.Equal(expectedMilliseconds, (int)SecretBudget.PerPattern(length).TotalMilliseconds);

    /// <summary>The budget never shrinks as the input grows, so a larger artifact is never harder.</summary>
    [Fact]
    public void The_budget_never_decreases_with_size()
    {
        var sizes = new[] { 0, 1, 1024, 64 * 1024, Mebibyte, 4 * Mebibyte, 16 * Mebibyte, 64 * Mebibyte };

        for (var i = 1; i < sizes.Length; i++)
        {
            Assert.True(SecretBudget.PerPattern(sizes[i]) >= SecretBudget.PerPattern(sizes[i - 1]));
            Assert.True(SecretBudget.Operation(sizes[i]) >= SecretBudget.Operation(sizes[i - 1]));
        }
    }

    /// <summary>
    /// The budget stops growing, and the operation stops sooner than the patterns would together.
    ///
    /// Without the second cap, seven patterns each entitled to the maximum would be seven times the
    /// maximum of somebody's machine for a single artifact.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(Mebibyte)]
    [InlineData(64 * Mebibyte)]
    [InlineData(int.MaxValue)]
    public void The_budget_is_capped_in_both_directions(int length)
    {
        var perPattern = SecretBudget.PerPattern(length);
        var operation = SecretBudget.Operation(length);

        Assert.True(perPattern >= SecretBudget.Base);
        Assert.True(perPattern <= SecretBudget.PerPatternMaximum);

        Assert.True(operation > TimeSpan.Zero);
        Assert.True(operation <= SecretBudget.OperationMaximum);

        // The operation can never be the sum of every pattern's maximum.
        Assert.True(operation < SecretBudget.PerPatternMaximum * SecretPatternCatalog.Patterns.Count);
    }

    /// <summary>The same length always produces the same budget; nothing here learns.</summary>
    [Fact]
    public void The_budget_is_deterministic()
    {
        foreach (var length in new[] { 0, 4096, Mebibyte, 16 * Mebibyte })
        {
            Assert.Equal(SecretBudget.PerPattern(length), SecretBudget.PerPattern(length));
            Assert.Equal(SecretBudget.Operation(length), SecretBudget.Operation(length));
        }
    }

    /// <summary>A pattern is never handed more than what is left of the operation.</summary>
    [Fact]
    public void No_pattern_is_given_more_than_the_operation_has_left()
    {
        var scope = SecretBudget.Open(16 * Mebibyte);

        foreach (var pattern in SecretPatternCatalog.Patterns)
        {
            var remaining = scope.Remaining;
            var regex = scope.Next(pattern);

            Assert.True(regex.MatchTimeout <= remaining);
            Assert.True(regex.MatchTimeout <= SecretBudget.PerPattern(16 * Mebibyte));
            Assert.True(regex.MatchTimeout > TimeSpan.Zero);
        }
    }

    /// <summary>
    /// An operation whose total is spent refuses the next pattern immediately.
    ///
    /// Deterministic without sleeping for seconds: the smallest input carries the smallest total,
    /// and consuming it is a matter of milliseconds rather than a wait anybody has to sit through.
    /// </summary>
    [Fact]
    public void An_exhausted_operation_refuses_the_next_pattern()
    {
        var scope = SecretBudget.Open(0);

        var spinner = Stopwatch.StartNew();

        while (scope.Remaining > TimeSpan.Zero && spinner.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(20);
        }

        Assert.True(scope.Remaining <= TimeSpan.Zero);

        Assert.Throws<RegexMatchTimeoutException>(() => scope.Next(SecretPatternCatalog.Patterns[0]));
    }

    // -- engine ----------------------------------------------------------------------------------

    /// <summary>
    /// Every pattern keeps exactly the options its shape needs, and nothing that changes meaning.
    ///
    /// The linear engine is absent by policy, not by omission: on this runtime it was measured to
    /// return no matches at all - silently - for several of these shapes past about a mebibyte when
    /// a finite timeout was set. The behavioural consequence is guarded separately below.
    /// </summary>
    [Fact]
    public void Every_pattern_keeps_the_options_it_needs_and_no_others()
    {
        foreach (var pattern in SecretPatternCatalog.Patterns)
        {
            var compiled = pattern.Compile(TimeSpan.FromSeconds(1));

            Assert.True(compiled.Options.HasFlag(RegexOptions.CultureInvariant));
            Assert.True(compiled.Options.HasFlag(RegexOptions.ExplicitCapture));

            Assert.False(compiled.Options.HasFlag(RegexOptions.NonBacktracking));
            Assert.False(compiled.Options.HasFlag(RegexOptions.Compiled));
            Assert.False(compiled.Options.HasFlag(RegexOptions.ECMAScript));
            Assert.False(compiled.Options.HasFlag(RegexOptions.RightToLeft));

            Assert.NotEqual(Regex.InfiniteMatchTimeout, compiled.MatchTimeout);
        }

        // Case insensitivity stays exactly where it already was.
        Assert.Equal(
            ["AUTHORIZATION", "PASSWORD"],
            SecretPatternCatalog.Patterns
                .Where(p => p.Options.HasFlag(RegexOptions.IgnoreCase))
                .Select(p => p.Name)
                .Order(StringComparer.Ordinal));
    }

    // -- the large-input regression ---------------------------------------------------------------

    /// <summary>
    /// A secret at the end of a large artifact is still found and still redacted.
    ///
    /// This is the guard that would fail if the linear engine were ever reintroduced. It asserts
    /// the security behaviour rather than the platform's bug: what matters is that a token at the
    /// tail of a mebibyte, four mebibytes or sixteen is detected, not why some other configuration
    /// would miss it.
    /// </summary>
    [Theory]
    [InlineData("JWT", Mebibyte)]
    [InlineData("JWT", 4 * Mebibyte)]
    [InlineData("JWT", 16 * Mebibyte)]
    [InlineData("OPENAI_TOKEN", Mebibyte)]
    [InlineData("OPENAI_TOKEN", 4 * Mebibyte)]
    [InlineData("OPENAI_TOKEN", 16 * Mebibyte)]
    [InlineData("GITHUB_TOKEN", Mebibyte)]
    [InlineData("GITHUB_TOKEN", 4 * Mebibyte)]
    [InlineData("GITHUB_TOKEN", 16 * Mebibyte)]
    [InlineData("AWS_ACCESS_KEY", Mebibyte)]
    [InlineData("AWS_ACCESS_KEY", 4 * Mebibyte)]
    [InlineData("AWS_ACCESS_KEY", 16 * Mebibyte)]
    [InlineData("PRIVATE_KEY", 4 * Mebibyte)]
    [InlineData("AUTHORIZATION", 4 * Mebibyte)]
    [InlineData("PASSWORD", 4 * Mebibyte)]
    public void A_secret_at_the_end_of_a_large_artifact_is_found(string family, int size)
    {
        var secret = Fake(family);
        var text = Filler(size) + "\n" + secret;

        var result = new EvidenceRedactor().Redact(text);

        Assert.Equal(1, result.RedactionCount);
        Assert.Contains("[REDACTED:" + family + "]", result.Text, StringComparison.Ordinal);

        // And the gate sees it too, at the same place.
        Assert.Contains(new CatalogSecretScanner().Scan(text), m => m.PatternName == family);
    }

    // -- parity ------------------------------------------------------------------------------------

    /// <summary>
    /// Every pattern matches exactly what it matched before, span for span.
    ///
    /// Compared against the same expression built the way the baseline built it - one fixed budget,
    /// same options - from the one catalogue. A second copy of the pattern text here would be the
    /// drift the catalogue exists to prevent.
    /// </summary>
    [Fact]
    public void Every_pattern_matches_exactly_what_it_did_before()
    {
        foreach (var pattern in SecretPatternCatalog.Patterns)
        {
            var baseline = new Regex(pattern.Expression, pattern.Options, TimeSpan.FromSeconds(20));
            var current = pattern.Compile(SecretBudget.PerPatternMaximum);

            foreach (var text in Corpus)
            {
                Assert.Equal(
                    baseline.Matches(text).Select(m => (m.Index, m.Length)),
                    current.Matches(text).Select(m => (m.Index, m.Length)));

                Assert.Equal(
                    baseline.Replace(text, pattern.Replacement),
                    current.Replace(text, pattern.Replacement));
            }
        }
    }

    /// <summary>
    /// The lookbehind shapes were not rewritten, so their offsets are exactly where they were.
    ///
    /// Turning a lookbehind into a consuming prefix would be far faster and would move every match
    /// offset - which is what the secret gate turns into a line number.
    /// </summary>
    [Theory]
    [InlineData("AUTHORIZATION", "Authorization: Bearer zzz")]
    [InlineData("AUTHORIZATION", "prefix\nAuthorization:\tvalue-here")]
    [InlineData("PASSWORD", "password=hunter2;rest")]
    [InlineData("PASSWORD", "PWD = spaced;")]
    public void The_lookbehind_shapes_match_where_they_always_did(string name, string text)
    {
        var pattern = SecretPatternCatalog.Patterns.Single(p => p.Name == name);

        var baseline = new Regex(pattern.Expression, pattern.Options, TimeSpan.FromSeconds(20)).Match(text);
        var current = pattern.Compile(SecretBudget.PerPattern(text.Length)).Match(text);

        Assert.True(baseline.Success);
        Assert.Equal(baseline.Index, current.Index);
        Assert.Equal(baseline.Length, current.Length);
        Assert.Equal(baseline.Value, current.Value);
    }

    /// <summary>
    /// The two lookbehind shapes match at exactly these offsets, written out.
    ///
    /// The parity test above compares the catalogue against itself, so it stays green if the
    /// expression is rewritten. These numbers do not: they are where the gate reports a finding
    /// today, and the fast rewrite everybody reaches for first - turning the lookbehind into a
    /// consuming prefix - moves every one of them.
    ///
    /// The value is included deliberately. .NET matches a lookbehind right to left and takes the
    /// shortest run of spaces, so the separating space belongs to the match rather than to the
    /// prefix. That is surprising, it is what the gate has always reported, and it is not being
    /// changed to make a benchmark look better.
    /// </summary>
    [Theory]
    [InlineData("AUTHORIZATION", "Authorization: Bearer zzz", 14, 11)]
    [InlineData("AUTHORIZATION", "prefix\nAuthorization:\tvalue-here", 21, 11)]
    [InlineData("PASSWORD", "password=hunter2;rest", 9, 7)]
    [InlineData("PASSWORD", "PWD = spaced;", 5, 7)]
    public void The_lookbehind_offsets_are_written_down(string name, string text, int index, int length)
    {
        var pattern = SecretPatternCatalog.Patterns.Single(p => p.Name == name);

        var match = pattern.Compile(SecretBudget.PerPattern(text.Length)).Match(text);

        Assert.True(match.Success);
        Assert.Equal(index, match.Index);
        Assert.Equal(length, match.Length);
        Assert.Equal(text.Substring(index, length), match.Value);

        // And the gate reports a finding in the same place.
        Assert.Contains(new SecretScanMatch(name, index), new CatalogSecretScanner().Scan(text));
    }

    // -- what the gate sees --------------------------------------------------------------------------

    /// <summary>
    /// The gate's findings are unchanged: same families, same offsets, same order.
    ///
    /// Evidence redaction and the gate read one catalogue, so a change made for one is a change made
    /// for both. Making storage work while quietly moving what K3 reports would be the worst
    /// possible way to pass a benchmark.
    /// </summary>
    [Fact]
    public void The_gates_findings_are_unchanged()
    {
        var scanner = new CatalogSecretScanner();

        foreach (var text in Corpus)
        {
            var expected = new List<SecretScanMatch>();

            foreach (var pattern in SecretPatternCatalog.Patterns)
            {
                var baseline = new Regex(pattern.Expression, pattern.Options, TimeSpan.FromSeconds(20));

                expected.AddRange(baseline.Matches(text).Select(m => new SecretScanMatch(pattern.Name, m.Index)));
            }

            Assert.Equal(
                expected
                    .OrderBy(m => m.CharacterOffset)
                    .ThenBy(m => m.PatternName, StringComparer.Ordinal)
                    .ToList(),
                scanner.Scan(text));
        }
    }

    /// <summary>The gate still recognises the same families it always did.</summary>
    [Fact]
    public void The_gates_family_names_are_unchanged() =>
        Assert.Equal(
            ["AUTHORIZATION", "AWS_ACCESS_KEY", "GITHUB_TOKEN", "JWT", "OPENAI_TOKEN", "PASSWORD", "PRIVATE_KEY"],
            new CatalogSecretScanner().PatternNames.Order(StringComparer.Ordinal));

    // -- redaction count -------------------------------------------------------------------------------

    /// <summary>The count is exact, across one, many, adjacent and mixed families.</summary>
    [Theory]
    [InlineData(1, "JWT")]
    [InlineData(3, "JWT", "JWT", "JWT")]
    [InlineData(2, "GITHUB_TOKEN", "AWS_ACCESS_KEY")]
    [InlineData(4, "JWT", "OPENAI_TOKEN", "GITHUB_TOKEN", "AWS_ACCESS_KEY")]
    public void The_redaction_count_is_exact(int expected, params string[] families)
    {
        var text = string.Join(" and ", families.Select(Fake));

        var result = new EvidenceRedactor().Redact(text);

        Assert.Equal(expected, result.RedactionCount);

        foreach (var family in families.Distinct(StringComparer.Ordinal))
        {
            Assert.Contains("[REDACTED:" + family + "]", result.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Adjacent occurrences are still two, not one.</summary>
    [Fact]
    public void Adjacent_secrets_are_counted_separately() =>
        Assert.Equal(
            2,
            new EvidenceRedactor().Redact(Fake("AWS_ACCESS_KEY") + " " + Fake("AWS_ACCESS_KEY")).RedactionCount);

    /// <summary>Many occurrences of one family are counted individually.</summary>
    [Fact]
    public void Many_occurrences_are_counted_individually() =>
        Assert.Equal(
            500,
            new EvidenceRedactor()
                .Redact(string.Join('\n', Enumerable.Repeat(Fake("GITHUB_TOKEN"), 500)))
                .RedactionCount);

    // -- the shape that backtracks ----------------------------------------------------------------------

    /// <summary>
    /// A small hostile artifact is refused, and refused on a small budget.
    ///
    /// The PEM shape can still be made to backtrack super-linearly - the expression was not
    /// rewritten, because rewriting it would move what the gate reports. What changed is what it is
    /// allowed to cost: a quarter mebibyte of unterminated headers now gets well under a second
    /// instead of two, and running out is a refusal rather than a partial result.
    /// </summary>
    [Fact]
    public void A_small_hostile_artifact_is_refused_on_a_small_budget()
    {
        var text = string.Concat(Enumerable.Repeat("-----BEGIN RSA PRIVATE KEY-----\n", 8192));

        // Well under the two seconds the old flat budget would have granted.
        Assert.True(SecretBudget.PerPattern(text.Length) < TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<RegexMatchTimeoutException>(() => new EvidenceRedactor().Redact(text));

        stopwatch.Stop();

        // A ceiling wide enough not to be a timing test, and far below the old operation's worst
        // case of seven patterns at two seconds each.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), stopwatch.Elapsed.ToString());
    }

    // -- large legitimate evidence -----------------------------------------------------------------------

    /// <summary>
    /// A sixteen mebibyte artifact the evidence contract accepts is stored rather than refused.
    ///
    /// The case that was red. Nothing about the storage ceiling changed - there is none - only how
    /// long the catalogue is allowed to look at what it is given.
    /// </summary>
    [Fact]
    public async Task A_large_legitimate_artifact_is_stored()
    {
        var text = Filler(16 * Mebibyte);
        var store = new EvidenceBlobStore(_root);

        var descriptor = await store.WriteTextAsync(
            text, EvidenceKind.TestReport, "text/plain", CancellationToken.None);

        Assert.Equal(0, descriptor.RedactionCount);
        Assert.Equal(text, await store.ReadTextAsync(descriptor.Sha256, CancellationToken.None));
    }

    /// <summary>
    /// A large artifact carrying secrets is stored with markers, wherever the secrets are.
    ///
    /// Beginning, middle, end and scattered, because a scan that gave up part way through would
    /// pass the first of those and fail the rest.
    /// </summary>
    [Theory]
    [InlineData("start", 1)]
    [InlineData("middle", 1)]
    [InlineData("end", 1)]
    [InlineData("scattered", 7)]
    public async Task A_large_artifact_with_secrets_is_stored_redacted(string placement, int expected)
    {
        var body = Filler(16 * Mebibyte);
        var secret = Fake("OPENAI_TOKEN");

        var text = placement switch
        {
            "start" => secret + "\n" + body,
            "middle" => body[..(body.Length / 2)] + "\n" + secret + "\n" + body[(body.Length / 2)..],
            "end" => body + "\n" + secret,
            _ => string.Join(
                "\n" + secret + "\n",
                Enumerable.Range(0, 8).Select(i => body[(i * (body.Length / 8))..((i + 1) * (body.Length / 8))])),
        };

        var store = new EvidenceBlobStore(_root);

        var descriptor = await store.WriteTextAsync(
            text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None);

        Assert.Equal(expected, descriptor.RedactionCount);

        var stored = await store.ReadTextAsync(descriptor.Sha256, CancellationToken.None);

        Assert.Contains("[REDACTED:OPENAI_TOKEN]", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
    }

    // -- failing closed -------------------------------------------------------------------------------------

    /// <summary>
    /// A redaction that runs out of time stores nothing at all.
    ///
    /// The seam is the redactor's own pattern list, which is how a test supplies a shape that cannot
    /// finish; the catalogue is untouched. What matters is the outcome: no blob, and above all no
    /// blob containing the value the redaction failed to remove.
    /// </summary>
    [Fact]
    public async Task A_redaction_that_runs_out_of_time_stores_nothing()
    {
        var secret = Fake("OPENAI_TOKEN");
        var text = secret + "\n" + Runaway.Input;

        var store = new EvidenceBlobStore(_root, new EvidenceRedactor([Runaway.Pattern]));

        await Assert.ThrowsAsync<RegexMatchTimeoutException>(
            () => store.WriteTextAsync(text, EvidenceKind.CommandStdout, "text/plain", CancellationToken.None));

        var files = Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(files);
    }

    /// <summary>
    /// A scan that runs out of time throws rather than reporting a clean diff.
    ///
    /// The direction of the failure is the whole point: "I did not finish" and "there is nothing
    /// here" are opposite answers, and a gate that confused them would pass exactly the change it
    /// exists to stop.
    /// </summary>
    [Fact]
    public void A_scan_that_runs_out_of_time_reports_nothing_clean() =>
        Assert.Throws<RegexMatchTimeoutException>(
            () => new CatalogSecretScanner([Runaway.Pattern]).Scan(Runaway.Input));

    /// <summary>A shape that cannot finish, for the two tests that need the clock to run out.</summary>
    private static class Runaway
    {
        public static SecretPattern Pattern { get; } = new(
            "TEST_RUNAWAY",
            @"(?:a|aa)+b",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            "[REDACTED:TEST_RUNAWAY]");

        public static string Input { get; } = new string('a', 40_000) + "c";
    }

    // -- small inputs ----------------------------------------------------------------------------------------

    /// <summary>Empty and tiny inputs keep working.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("nothing to see")]
    public void Small_inputs_are_unaffected(string text)
    {
        var result = new EvidenceRedactor().Redact(text);

        Assert.Equal(text, result.Text);
        Assert.Equal(0, result.RedactionCount);
    }

    /// <summary>A tiny secret-bearing input is still redacted, and quickly.</summary>
    [Fact]
    public void A_small_secret_bearing_input_is_still_redacted()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = new EvidenceRedactor().Redact("token " + Fake("JWT"));

        stopwatch.Stop();

        Assert.Equal(1, result.RedactionCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
    }

    // -- one authority ---------------------------------------------------------------------------------------

    /// <summary>
    /// The redactor and the gate read the same catalogue and the same budget.
    ///
    /// Two lists would be two definitions of what a secret is, and the weaker one would decide what
    /// reached disk.
    /// </summary>
    [Fact]
    public void The_redactor_and_the_gate_share_one_authority()
    {
        Assert.Equal(SecretPatternCatalog.Names, new CatalogSecretScanner().PatternNames);

        foreach (var text in Corpus.Where(t => t.Length > 0))
        {
            var redacted = new EvidenceRedactor().Redact(text);
            var found = new CatalogSecretScanner().Scan(text);

            Assert.Equal(found.Count, redacted.RedactionCount);
        }
    }
}
