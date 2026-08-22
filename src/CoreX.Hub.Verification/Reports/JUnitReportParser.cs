using System.Globalization;
using System.Xml.Linq;

namespace CoreX.Hub.Verification.Reports;

/// <summary>
/// Bounded JUnit style XML reader supporting the two shapes tools actually emit: a single
/// testsuite root, or a testsuites root wrapping one or more suites.
///
/// Vendor specific extensions are deliberately out of scope; unknown optional attributes
/// are ignored. A report missing the structure the counters depend on fails rather than
/// reporting zero passes as success.
/// </summary>
public static class JUnitReportParser
{
    public static ReportParseResult Parse(string xml)
    {
        var load = SafeXml.TryLoad(xml, out var document);
        if (!load.Succeeded || document?.Root is null)
        {
            return load.Succeeded
                ? ReportParseResult.Failed(ReportParseFailure.MalformedXml, "the report had no root element")
                : load;
        }

        var root = document.Root;
        var rootName = root.Name.LocalName;

        var isSuites = string.Equals(rootName, "testsuites", StringComparison.OrdinalIgnoreCase);
        var isSuite = string.Equals(rootName, "testsuite", StringComparison.OrdinalIgnoreCase);

        if (!isSuites && !isSuite)
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                $"expected a testsuite or testsuites root element but found '{rootName}'");
        }

        // An aggregating root that already carries the totals is trusted as written.
        if (isSuites && root.HasAttribute("tests"))
        {
            return Build(root, ReadDuration(root), null);
        }

        // Otherwise sum the leaf suites, so a nested suite is not counted twice through
        // its parent.
        var leaves = LeafSuites(root).ToList();
        if (leaves.Count == 0)
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                "the report declares no testsuite element carrying counters");
        }

        var missing = leaves.Where(s => !s.HasAttribute("tests")).ToList();
        if (missing.Count == leaves.Count)
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                "no testsuite element declares a tests attribute");
        }

        var total = leaves.Sum(s => s.ReadInt("tests"));
        var failures = leaves.Sum(s => s.ReadInt("failures"));
        var errors = leaves.Sum(s => s.ReadInt("errors"));
        var skipped = leaves.Sum(s => s.ReadInt("skipped"));

        var diagnostic = missing.Count == 0
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{missing.Count} testsuite element(s) declared no tests attribute and contributed nothing");

        return Summarise(total, failures, errors, skipped, ReadDuration(root), diagnostic);
    }

    private static ReportParseResult Build(XElement element, TimeSpan? duration, string? diagnostic) =>
        Summarise(
            element.ReadInt("tests"),
            element.ReadInt("failures"),
            element.ReadInt("errors"),
            element.ReadInt("skipped"),
            duration,
            diagnostic);

    private static ReportParseResult Summarise(
        int total,
        int failures,
        int errors,
        int skipped,
        TimeSpan? duration,
        string? diagnostic)
    {
        var passed = total - failures - errors - skipped;

        if (passed < 0)
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the counters are inconsistent: tests={total} but failures+errors+skipped={failures + errors + skipped}"));
        }

        return ReportParseResult.Ok(new TestSummary
        {
            Format = TestReportFormat.JUnit,
            Total = total,
            Passed = passed,
            Failed = failures,
            Errors = errors,
            Skipped = skipped,
            Duration = duration,
            Diagnostic = diagnostic,
        });
    }

    /// <summary>Suites that contain no nested suite of their own.</summary>
    private static IEnumerable<XElement> LeafSuites(XElement root)
    {
        var suites = root.ByLocalName("testsuite").ToList();

        if (string.Equals(root.Name.LocalName, "testsuite", StringComparison.OrdinalIgnoreCase))
        {
            suites.Insert(0, root);
        }

        return suites.Where(s => !s.ByLocalName("testsuite").Any());
    }

    private static TimeSpan? ReadDuration(XElement element)
    {
        var raw = element.AttributeValue("time");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
