using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// When a set of reviewer findings blocks (architecture J.4).
///
/// The rule is about the collection, which is the whole reason it lives in one place: a
/// Critical blocks alone, Major blocks by count, and Minor and Info never block however
/// many of them there are. Asking each finding whether it blocks could not express that
/// without lying about one of the three.
/// </summary>
public sealed class ReviewFindingGateTests
{
    private static Finding Finding(FindingSeverity severity, bool outOfScope = false) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            severity,
            FindingCategory.Correctness,
            $"A {severity} concern",
            "It would break on the empty input.",
            "src/thing.cs",
            12,
            null,
            FindingConfidence.High,
            outOfScope,
            DomainFixtures.Now);

    [Fact]
    public void One_in_scope_critical_blocks_on_its_own()
    {
        Assert.True(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Critical)]));
    }

    [Fact]
    public void One_in_scope_major_blocks_under_the_current_threshold()
    {
        // The frozen default is 1. Calibrating it is G7-1, in Phase 7, and it is read from
        // the gate here rather than written into the test, so a future change moves one
        // number in one place.
        Assert.Equal(1, ReviewFindingGate.MajorThreshold);

        var majors = Enumerable.Range(0, ReviewFindingGate.MajorThreshold)
            .Select(_ => Finding(FindingSeverity.Major))
            .ToList();

        Assert.True(ReviewFindingGate.HasBlockingFindings(majors));
    }

    [Fact]
    public void A_finding_outside_the_diff_never_blocks_however_severe()
    {
        // It is an observation about code this execution did not touch.
        Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Critical, outOfScope: true)]));
        Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Major, outOfScope: true)]));

        Assert.False(Finding(FindingSeverity.Critical, outOfScope: true).ParticipatesInReview);
        Assert.True(Finding(FindingSeverity.Critical).ParticipatesInReview);
    }

    [Theory]
    [InlineData(FindingSeverity.Minor)]
    [InlineData(FindingSeverity.Info)]
    public void Minor_and_info_never_block_on_any_count(FindingSeverity severity)
    {
        var many = Enumerable.Range(0, 50).Select(_ => Finding(severity)).ToList();

        Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(severity)]));
        Assert.False(ReviewFindingGate.HasBlockingFindings(many));
    }

    [Fact]
    public void Minor_and_info_together_still_do_not_block()
    {
        Assert.False(ReviewFindingGate.HasBlockingFindings(
        [
            Finding(FindingSeverity.Minor),
            Finding(FindingSeverity.Info),
            Finding(FindingSeverity.Minor),
        ]));
    }

    [Fact]
    public void Nothing_at_all_does_not_block()
    {
        Assert.False(ReviewFindingGate.HasBlockingFindings([]));
    }

    [Fact]
    public void An_out_of_scope_critical_beside_a_minor_still_does_not_block()
    {
        // Neither counts, and neither makes the other count.
        Assert.False(ReviewFindingGate.HasBlockingFindings(
        [
            Finding(FindingSeverity.Critical, outOfScope: true),
            Finding(FindingSeverity.Minor),
        ]));
    }

    [Fact]
    public void The_description_says_what_was_counted()
    {
        // A refusal has to be explicable, so the gate can describe its own answer.
        var described = ReviewFindingGate.Describe(
        [
            Finding(FindingSeverity.Minor),
            Finding(FindingSeverity.Critical, outOfScope: true),
        ]);

        Assert.Contains("0 in-scope Critical", described, StringComparison.Ordinal);
        Assert.Contains("0 in-scope Major", described, StringComparison.Ordinal);
        Assert.Contains("1 in-scope in total", described, StringComparison.Ordinal);
    }
}
