using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Baseline;

/// <summary>
/// What the cache remembers for one baseline key: the machine counters observed when the
/// profile last ran against the pinned base commit.
///
/// This is evidence, not a verdict. Nothing stored here can make a currently failing
/// required step pass.
/// </summary>
public sealed record BaselineEntry
{
    public required BaselineKey Key { get; init; }

    public required TestSummary Summary { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>The step whose report produced the counters, for traceability.</summary>
    public required string StepName { get; init; }
}
