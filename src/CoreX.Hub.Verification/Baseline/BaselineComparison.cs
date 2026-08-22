using System.Globalization;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Baseline;

/// <summary>A small closed vocabulary. Deliberately not a quality score.</summary>
public enum BaselineVerdict
{
    NoBaseline,
    Improved,
    Regressed,
    Unchanged,
}

public sealed record BaselineComparisonResult
{
    public required BaselineVerdict Verdict { get; init; }

    /// <summary>Positive means more failures than the baseline had.</summary>
    public int FailedDelta { get; init; }

    public int PassedDelta { get; init; }

    /// <summary>The baseline was failing and the current run is not.</summary>
    public bool FixedAllFailures { get; init; }

    /// <summary>The current run fails more than the baseline did.</summary>
    public bool IntroducedNewFailures { get; init; }

    public required string Description { get; init; }
}

/// <summary>
/// Compares current counters with a cached baseline.
///
/// The comparison is context for a human and for the evidence record. It never feeds back
/// into whether a required step passed: a run with fewer failures than the baseline still
/// failed if it failed (Phase 4 brief section 23).
/// </summary>
public static class BaselineComparer
{
    public static BaselineComparisonResult Compare(TestSummary? baseline, TestSummary current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (baseline is null)
        {
            return new BaselineComparisonResult
            {
                Verdict = BaselineVerdict.NoBaseline,
                Description = "no baseline was available for this repository, base commit and profile",
            };
        }

        var failedDelta = current.FailedOrErrored - baseline.FailedOrErrored;
        var passedDelta = current.Passed - baseline.Passed;

        var verdict = failedDelta switch
        {
            < 0 => BaselineVerdict.Improved,
            > 0 => BaselineVerdict.Regressed,
            _ => passedDelta > 0 ? BaselineVerdict.Improved
                : passedDelta < 0 ? BaselineVerdict.Regressed
                : BaselineVerdict.Unchanged,
        };

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"base {baseline.Passed} passed / {baseline.FailedOrErrored} failed -> now {current.Passed} passed / {current.FailedOrErrored} failed ({verdict})");

        return new BaselineComparisonResult
        {
            Verdict = verdict,
            FailedDelta = failedDelta,
            PassedDelta = passedDelta,
            FixedAllFailures = baseline.FailedOrErrored > 0 && current.FailedOrErrored == 0,
            IntroducedNewFailures = failedDelta > 0,
            Description = description,
        };
    }
}
