using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Parser behaviour on report text. These are the counters a gate later depends on, so an
/// unrecognised shape fails rather than reporting a convenient zero.
/// </summary>
public sealed class ReportParserTests
{
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Fact]
    public void Trx_counters_are_read_from_the_report()
    {
        var xml =
            $"<TestRun xmlns=\"{TrxNamespace}\">"
            + "<Times start=\"2026-01-01T10:00:00.0000000+00:00\" finish=\"2026-01-01T10:00:12.0000000+00:00\" />"
            + "<ResultSummary><Counters total=\"14\" executed=\"12\" passed=\"11\" failed=\"1\" "
            + "error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" notExecuted=\"2\" /></ResultSummary>"
            + "</TestRun>";

        var result = TrxReportParser.Parse(xml);

        Assert.True(result.Succeeded, result.Describe());
        var summary = result.Summary!;
        Assert.Equal(TestReportFormat.Trx, summary.Format);
        Assert.Equal(14, summary.Total);
        Assert.Equal(11, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(TimeSpan.FromSeconds(12), summary.Duration);
        Assert.Null(summary.Diagnostic);
    }

    [Fact]
    public void A_trx_without_counters_is_summarised_from_the_individual_outcomes()
    {
        var xml =
            $"<TestRun xmlns=\"{TrxNamespace}\"><Results>"
            + "<UnitTestResult outcome=\"Passed\" />"
            + "<UnitTestResult outcome=\"Passed\" />"
            + "<UnitTestResult outcome=\"Failed\" />"
            + "<UnitTestResult outcome=\"NotExecuted\" />"
            + "</Results></TestRun>";

        var summary = TrxReportParser.Parse(xml).Summary!;

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void An_unrecognised_trx_outcome_is_never_counted_as_a_pass()
    {
        var xml =
            $"<TestRun xmlns=\"{TrxNamespace}\"><Results>"
            + "<UnitTestResult outcome=\"Passed\" />"
            + "<UnitTestResult outcome=\"Interstellar\" />"
            + "<UnitTestResult />"
            + "</Results></TestRun>";

        var summary = TrxReportParser.Parse(xml).Summary!;

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(2, summary.Errors);
        Assert.NotNull(summary.Diagnostic);
        Assert.Contains("Interstellar", summary.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("(missing)", summary.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void JUnit_counters_are_read_from_the_aggregating_root()
    {
        var xml =
            "<testsuites tests=\"20\" failures=\"3\" errors=\"1\" skipped=\"2\" time=\"7.25\">"
            + "<testsuite name=\"a\" tests=\"20\" failures=\"3\" errors=\"1\" skipped=\"2\" />"
            + "</testsuites>";

        var summary = JUnitReportParser.Parse(xml).Summary!;

        Assert.Equal(TestReportFormat.JUnit, summary.Format);
        Assert.Equal(20, summary.Total);
        Assert.Equal(14, summary.Passed);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(TimeSpan.FromSeconds(7.25), summary.Duration);
    }

    [Fact]
    public void Nested_JUnit_suites_are_aggregated_from_the_leaves_without_double_counting()
    {
        var xml =
            "<testsuites>"
            + "<testsuite name=\"outer\">"
            + "<testsuite name=\"leaf-1\" tests=\"5\" failures=\"1\" errors=\"0\" skipped=\"1\" />"
            + "<testsuite name=\"leaf-2\" tests=\"4\" failures=\"0\" errors=\"1\" skipped=\"0\" />"
            + "</testsuite>"
            + "</testsuites>";

        var summary = JUnitReportParser.Parse(xml).Summary!;

        Assert.Equal(9, summary.Total);
        Assert.Equal(6, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void A_single_JUnit_testsuite_root_is_supported()
    {
        var summary = JUnitReportParser
            .Parse("<testsuite name=\"solo\" tests=\"3\" failures=\"1\" errors=\"0\" skipped=\"0\" />")
            .Summary!;

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public void A_malformed_trx_fails_to_parse()
    {
        var result = TrxReportParser.Parse("<TestRun><ResultSummary><Counters total=\"3\" ");

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.MalformedXml, result.Failure);
    }

    [Fact]
    public void A_malformed_JUnit_report_fails_to_parse()
    {
        var result = JUnitReportParser.Parse("<testsuites><testsuite name=\"a\"></testsuites>");

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.MalformedXml, result.Failure);
    }

    [Fact]
    public void A_report_with_the_wrong_root_element_fails_rather_than_reporting_zero()
    {
        var trx = TrxReportParser.Parse("<Nonsense />");
        Assert.False(trx.Succeeded);
        Assert.Equal(ReportParseFailure.UnexpectedStructure, trx.Failure);

        var junit = JUnitReportParser.Parse("<Nonsense />");
        Assert.False(junit.Succeeded);
        Assert.Equal(ReportParseFailure.UnexpectedStructure, junit.Failure);
    }

    [Fact]
    public void Inconsistent_JUnit_counters_are_refused()
    {
        var result = JUnitReportParser.Parse(
            "<testsuite tests=\"2\" failures=\"3\" errors=\"0\" skipped=\"0\" />");

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.UnexpectedStructure, result.Failure);
    }
}
