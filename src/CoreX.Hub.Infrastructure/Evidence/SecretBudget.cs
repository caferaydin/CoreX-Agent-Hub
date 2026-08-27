using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CoreX.Hub.Infrastructure.Evidence;

/// <summary>
/// How long the secret catalogue is allowed to look at one artifact.
///
/// A timeout exists to stop a pattern running away, and one fixed number cannot do that job,
/// because two very different things look identical from behind a stopwatch. Catastrophic
/// backtracking is super-linear: it blows through any budget on a small input, and it is what the
/// bound is for. Scanning a large artifact is linear: it is slow only in proportion to size, and an
/// artifact's size is the producer's business. A flat number has to be either too small for the
/// second or far too generous for the first, and two seconds was the first of those - a sixteen
/// mebibyte test report, which the evidence contract accepts, cost about eight hundred milliseconds
/// per pattern on a quiet machine and threw on a busy one.
///
/// So the budget grows with the input and stops growing. A small input gets a small budget, which
/// is what keeps a hostile one cheap; a large one gets proportionally more, which is what lets
/// legitimate evidence through; and the ceiling stops the proportion from becoming an argument for
/// minutes of somebody's machine.
///
/// The second bound is the one a per-pattern timeout cannot give. Seven patterns each entitled to
/// the maximum would be seven times the maximum for a single artifact, so an operation carries a
/// total as well and every pattern is bounded by whatever is left of it.
///
/// These are product security constants. They are deliberately not configuration: an artifact that
/// could ask for more time would be an artifact that could ask to be examined less carefully.
/// </summary>
public static class SecretBudget
{
    /// <summary>
    /// What any input is worth before its size is considered.
    ///
    /// Covers the fixed work - building the expression, starting the scan - so a tiny artifact is
    /// not judged against a budget of nearly nothing.
    /// </summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// What each mebibyte adds.
    ///
    /// Measured: the two patterns that dominate cost about fifty milliseconds per mebibyte on
    /// ordinary text, and about four times that on text shaped to be awkward for them - many
    /// repeated <c>password=</c> prefixes, which a build log can legitimately contain. Three
    /// hundred leaves the awkward-but-legitimate case room and still scales down.
    /// </summary>
    public static readonly TimeSpan PerMebibyte = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The most any single pattern may ever have, whatever the input size.
    ///
    /// Reached at about twenty five mebibytes. Chosen against the measured worst legitimate pattern
    /// cost at sixteen mebibytes - roughly eight hundred milliseconds - so it is generous by a wide
    /// margin without being the kind of number that makes exhausting a machine cheap.
    /// </summary>
    public static readonly TimeSpan PerPatternMaximum = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The most one whole redaction or scan may ever have.
    ///
    /// Not the sum of the per-pattern maxima. Only two of the seven patterns can consume meaningful
    /// time, so a total that carries two of them carries the operation - and it caps the worst case
    /// at twelve seconds rather than the fifty six that seven independent maxima would allow.
    /// </summary>
    public static readonly TimeSpan OperationMaximum = TimeSpan.FromSeconds(12);

    /// <summary>How many patterns the total is sized to carry at full stretch.</summary>
    public const int CostlyPatternCount = 2;

    private const int BytesPerMebibyte = 1024 * 1024;

    /// <summary>
    /// The budget one pattern gets for an input of this length.
    ///
    /// Deterministic: the same length always produces the same answer. Nothing here adapts to how
    /// fast this machine is or to what happened last time - a budget that learned would be a budget
    /// that could be taught.
    /// </summary>
    public static TimeSpan PerPattern(int inputLength)
    {
        // Computed in double from a non-negative length, so a length near int.MaxValue produces a
        // large number rather than a wrapped one and the clamp decides what it means.
        var mebibytes = Math.Max(inputLength, 0) / (double)BytesPerMebibyte;

        var milliseconds = Base.TotalMilliseconds + (PerMebibyte.TotalMilliseconds * mebibytes);

        return TimeSpan.FromMilliseconds(
            Math.Clamp(milliseconds, Base.TotalMilliseconds, PerPatternMaximum.TotalMilliseconds));
    }

    /// <summary>The budget one whole operation gets for an input of this length.</summary>
    public static TimeSpan Operation(int inputLength)
    {
        var milliseconds = PerPattern(inputLength).TotalMilliseconds * CostlyPatternCount;

        return TimeSpan.FromMilliseconds(
            Math.Clamp(milliseconds, Base.TotalMilliseconds, OperationMaximum.TotalMilliseconds));
    }

    /// <summary>Opens a budget for one redaction or one scan.</summary>
    public static SecretBudgetScope Open(int inputLength) => new(inputLength);
}

/// <summary>
/// One artifact's share of time, spent down as the patterns run.
///
/// Asking for the remaining total before each pattern - rather than checking a stopwatch afterwards
/// - is the difference between bounding an operation and merely noticing that it went long. A
/// timeout handed to the expression is enforced while it matches; a check after the fact cannot
/// interrupt anything that is still running.
/// </summary>
public sealed class SecretBudgetScope
{
    /// <summary>
    /// The steps an effective budget is rounded down to.
    ///
    /// Every distinct timeout needs its own expression instance, because the platform fixes the
    /// timeout when the expression is built. Without a ladder that would be a fresh construction
    /// per pattern per operation - measured at one and a half to two and a half milliseconds each,
    /// which for a gate that scans one block per changed file is seconds across a large change.
    /// Rounding down never grants more time than the budget allows, and the ladder keeps the cache
    /// finite: ten steps, seven patterns, seventy entries at the very most.
    /// </summary>
    private static readonly TimeSpan[] Ladder =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12),
    ];

    /// <summary>One expression per pattern per ladder step. Bounded by construction.</summary>
    private static readonly ConcurrentDictionary<(string Pattern, long Ticks), Regex> Compiled = new();

    /// <summary>Monotonic: a clock that can go backwards would be a budget that could be extended.</summary>
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    private readonly TimeSpan _perPattern;
    private readonly TimeSpan _total;

    internal SecretBudgetScope(int inputLength)
    {
        _perPattern = SecretBudget.PerPattern(inputLength);
        _total = SecretBudget.Operation(inputLength);
    }

    /// <summary>What is left of the operation's total.</summary>
    public TimeSpan Remaining => _total - _elapsed.Elapsed;

    /// <summary>
    /// The expression to use for the next pattern, already bounded by what is left.
    ///
    /// Fails closed rather than skipping: an operation that ran out of time has not established
    /// that the artifact is free of secrets, and continuing without this pattern would be storing
    /// something nobody checked.
    /// </summary>
    public Regex Next(SecretPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var remaining = Remaining;

        if (remaining <= TimeSpan.Zero)
        {
            throw new RegexMatchTimeoutException(string.Empty, pattern.Expression, _total);
        }

        var effective = Round(remaining < _perPattern ? remaining : _perPattern);

        // Keyed only by the pattern's own name and the quantised budget, both of which the Hub
        // decides. Nothing about the artifact reaches this key.
        return Compiled.GetOrAdd((pattern.Name, effective.Ticks), _ => pattern.Compile(effective));
    }

    /// <summary>Rounds down to a ladder step, so a budget is never rounded up into more time.</summary>
    private static TimeSpan Round(TimeSpan requested)
    {
        // Below the smallest step there is still work to do and the expression needs a positive
        // timeout, so what is left is used exactly rather than rounded to nothing.
        if (requested < Ladder[0])
        {
            return requested;
        }

        var step = Ladder[0];

        foreach (var candidate in Ladder)
        {
            if (candidate <= requested)
            {
                step = candidate;
            }
        }

        return step;
    }
}
