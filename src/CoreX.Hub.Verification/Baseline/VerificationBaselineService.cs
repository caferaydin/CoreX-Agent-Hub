using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Baseline;

public sealed record BaselineLookup(BaselineEntry? Entry, bool CacheHit)
{
    public static BaselineLookup Miss { get; } = new(null, false);
}

/// <summary>
/// Answers "what did this profile score on this base commit last time" and caches it.
///
/// The module deliberately does not know how a baseline working tree is obtained: it does
/// not create git worktrees and does not know the workspace module. A caller supplies a
/// producer, and Phase 5 decides what that producer does (Phase 4 brief section 24).
/// </summary>
public sealed class VerificationBaselineService
{
    private readonly IVerificationBaselineCache _cache;
    private readonly TimeProvider _clock;

    public VerificationBaselineService(IVerificationBaselineCache cache, TimeProvider? clock = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads the cache only. Used when the caller has no way to produce a baseline.</summary>
    public Task<BaselineEntry?> TryGetAsync(BaselineKey key, CancellationToken cancellationToken) =>
        _cache.TryGetAsync(key, cancellationToken);

    /// <summary>
    /// Returns the cached baseline, or asks <paramref name="produceBaseline"/> for one and
    /// caches what it produced. A producer that returns no counters caches nothing, so a
    /// failed baseline attempt is retried rather than remembered as an empty result.
    /// </summary>
    public async Task<BaselineLookup> GetOrCaptureAsync(
        BaselineKey key,
        Func<CancellationToken, Task<VerificationRunResult?>> produceBaseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(produceBaseline);

        var cached = await _cache.TryGetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return new BaselineLookup(cached, true);
        }

        var produced = await produceBaseline(cancellationToken);
        var summary = produced?.TestSummary;

        if (produced is null || summary is null)
        {
            return BaselineLookup.Miss;
        }

        var entry = new BaselineEntry
        {
            Key = key,
            Summary = summary,
            CapturedAtUtc = _clock.GetUtcNow(),
            StepName = produced.Steps.LastOrDefault(s => s.TestSummary is not null)?.StepName ?? string.Empty,
        };

        await _cache.SaveAsync(entry, cancellationToken);
        return new BaselineLookup(entry, false);
    }

    /// <summary>
    /// Attaches a comparison to a finished run. The run's own required outcome is copied
    /// through untouched: a baseline is context, never permission.
    /// </summary>
    public static VerificationRunResult Attach(VerificationRunResult run, TestSummary? baseline)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.TestSummary is null
            ? run
            : run with { BaselineComparison = BaselineComparer.Compare(baseline, run.TestSummary) };
    }
}
