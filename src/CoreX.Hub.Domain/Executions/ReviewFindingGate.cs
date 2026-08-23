namespace CoreX.Hub.Domain.Executions;

/// <summary>
/// Whether a set of reviewer findings blocks (architecture J.4).
///
/// One authority, deliberately. The rule is about a collection, not about a finding: a
/// single Critical blocks on its own, but Major blocks by count, so no per-finding boolean
/// can express it honestly. Asking each finding "are you blocking?" would have to answer
/// yes for a lone Major today and could not stay true if the threshold ever moved.
///
/// It is a lookup, not a policy engine: no rules language, no plugins, no configuration.
/// The reviewer that produces findings is Phase 7; this is only the gate their output is
/// read through, and both the review step and a human override read it here rather than
/// each keeping a copy.
/// </summary>
public static class ReviewFindingGate
{
    /// <summary>
    /// How many in-scope Major findings block. The frozen default is 1.
    ///
    /// Calibrating it against real reviewer output is G7-1, in Phase 7. That work belongs
    /// here, changing this number, and nowhere else - which is the reason the number lives
    /// in one place instead of being written into each caller.
    /// </summary>
    public const int MajorThreshold = 1;

    /// <summary>
    /// Whether a finding counts at all.
    ///
    /// A finding anchored outside the diff is an observation about code this execution did
    /// not touch. It is recorded because it is worth knowing, and it never blocks, because
    /// blocking on it would make an execution answerable for work it never did.
    /// </summary>
    public static bool ParticipatesInReview(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return !finding.OutOfScope;
    }

    /// <summary>
    /// The J.4 gate: one in-scope Critical, or enough in-scope Majors. Minor and Info are
    /// reported and never block on any count.
    /// </summary>
    public static bool HasBlockingFindings(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var critical = 0;
        var major = 0;

        foreach (var finding in findings.Where(ParticipatesInReview))
        {
            switch (finding.Severity)
            {
                case FindingSeverity.Critical:
                    critical++;
                    break;
                case FindingSeverity.Major:
                    major++;
                    break;
                default:
                    // Minor and Info are visible in the review and decide nothing.
                    break;
            }
        }

        return critical >= 1 || major >= MajorThreshold;
    }

    /// <summary>Why the gate answered as it did, for a person reading a refusal.</summary>
    public static string Describe(IEnumerable<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var counted = findings.Where(ParticipatesInReview).ToList();

        var critical = counted.Count(f => f.Severity == FindingSeverity.Critical);
        var major = counted.Count(f => f.Severity == FindingSeverity.Major);

        return $"{critical} in-scope Critical, {major} in-scope Major "
               + $"(threshold {MajorThreshold}), {counted.Count} in-scope in total";
    }
}
