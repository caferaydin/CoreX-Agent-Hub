using System.Globalization;
using System.Xml.Linq;

namespace CoreX.Hub.Verification.Reports;

/// <summary>
/// Bounded TRX reader. It parses the XML report, never the locale formatted summary a
/// runner prints on the console.
///
/// Two sources of counters exist in a TRX file. The ResultSummary/Counters element is
/// authoritative when present; otherwise the individual result outcomes are counted. An
/// outcome this parser does not recognise is never silently treated as a pass.
/// </summary>
public static class TrxReportParser
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
        if (!string.Equals(root.Name.LocalName, "TestRun", StringComparison.OrdinalIgnoreCase))
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                $"expected a TestRun root element but found '{root.Name.LocalName}'");
        }

        var duration = ReadDuration(root);
        var counters = root.ByLocalName("Counters").FirstOrDefault();

        return counters is not null
            ? FromCounters(counters, duration)
            : FromOutcomes(root, duration);
    }

    private static ReportParseResult FromCounters(XElement counters, TimeSpan? duration)
    {
        if (!counters.HasAttribute("total"))
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                "the Counters element does not declare a total");
        }

        var total = counters.ReadInt("total");
        var passed = counters.ReadInt("passed");

        // A timed out or aborted test is a failure, not an error in the report sense.
        var failed = counters.ReadInt("failed") + counters.ReadInt("timeout") + counters.ReadInt("aborted");
        var errors = counters.ReadInt("error");
        var skipped = counters.ReadInt("notExecuted") + counters.ReadInt("inconclusive");

        string? diagnostic = null;
        var accounted = passed + failed + errors + skipped;
        if (accounted != total)
        {
            diagnostic = string.Create(
                CultureInfo.InvariantCulture,
                $"TRX counters do not add up: total={total} but passed+failed+errors+skipped={accounted}");
        }

        return ReportParseResult.Ok(new TestSummary
        {
            Format = TestReportFormat.Trx,
            Total = total,
            Passed = passed,
            Failed = failed,
            Errors = errors,
            Skipped = skipped,
            Duration = duration,
            Diagnostic = diagnostic,
        });
    }

    private static ReportParseResult FromOutcomes(XElement root, TimeSpan? duration)
    {
        var results = root.ByLocalName("UnitTestResult").ToList();
        if (results.Count == 0)
        {
            return ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure,
                "the report declares neither Counters nor any UnitTestResult");
        }

        int passed = 0, failed = 0, errors = 0, skipped = 0;
        var unmapped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            var outcome = result.AttributeValue("outcome")?.Trim() ?? string.Empty;

            switch (outcome.ToLowerInvariant())
            {
                case "passed":
                    passed++;
                    break;
                case "failed":
                case "timeout":
                case "aborted":
                    failed++;
                    break;
                case "error":
                    errors++;
                    break;
                case "notexecuted":
                case "inconclusive":
                case "disconnected":
                case "warning":
                case "pending":
                case "notrunnable":
                    skipped++;
                    break;
                default:
                    // Never a pass. An unknown outcome is counted as an error and named.
                    errors++;
                    unmapped.Add(string.IsNullOrEmpty(outcome) ? "(missing)" : outcome);
                    break;
            }
        }

        var diagnostic = unmapped.Count == 0
            ? null
            : "unmapped TRX outcome(s) counted as errors: " + string.Join(", ", unmapped);

        return ReportParseResult.Ok(new TestSummary
        {
            Format = TestReportFormat.Trx,
            Total = results.Count,
            Passed = passed,
            Failed = failed,
            Errors = errors,
            Skipped = skipped,
            Duration = duration,
            Diagnostic = diagnostic,
        });
    }

    private static TimeSpan? ReadDuration(XElement root)
    {
        var times = root.ByLocalName("Times").FirstOrDefault();
        if (times is null)
        {
            return null;
        }

        var start = times.AttributeValue("start");
        var finish = times.AttributeValue("finish");

        return DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out var from)
               && DateTimeOffset.TryParse(finish, CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out var to)
               && to >= from
            ? to - from
            : null;
    }
}
