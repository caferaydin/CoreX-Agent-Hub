using System.Runtime.Versioning;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// Test reports are repository produced input, so the parser must not be a way to read
/// arbitrary files or reach the network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class XmlSecurityTests
{
    [Fact]
    public void A_report_declaring_a_dtd_is_refused()
    {
        var xml =
            "<?xml version=\"1.0\"?>"
            + "<!DOCTYPE TestRun [<!ENTITY harmless \"x\">]>"
            + "<TestRun><ResultSummary><Counters total=\"1\" passed=\"1\" /></ResultSummary></TestRun>";

        var result = TrxReportParser.Parse(xml);

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.ProhibitedXmlConstruct, result.Failure);
    }

    [Fact]
    public void An_external_entity_is_never_resolved()
    {
        // The classic XXE shape. It is refused at the DTD, so the referenced file is never
        // opened and its content can never reach evidence.
        var xml =
            "<?xml version=\"1.0\"?>"
            + "<!DOCTYPE testsuites [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>"
            + "<testsuites tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\">"
            + "<testsuite name=\"&xxe;\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" />"
            + "</testsuites>";

        var result = JUnitReportParser.Parse(xml);

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.ProhibitedXmlConstruct, result.Failure);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void An_external_entity_reference_without_a_local_dtd_is_still_not_resolved()
    {
        // No DOCTYPE, so nothing is prohibited outright - but an undeclared entity must
        // still fail rather than be fetched.
        var xml =
            "<testsuites tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\">"
            + "<testsuite name=\"&remote;\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" />"
            + "</testsuites>";

        var result = JUnitReportParser.Parse(xml);

        Assert.False(result.Succeeded);
        Assert.Equal(ReportParseFailure.MalformedXml, result.Failure);
    }

    [Fact]
    public async Task An_oversized_report_is_refused_without_being_loaded()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "big.xml", "huge"],
                timeoutSeconds: 120,
                parser: VerificationResultParser.JUnit,
                resultPath: "big.xml"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        var step = run.Steps[0];
        Assert.Equal(VerificationStepStatus.ParseFailed, step.Status);
        Assert.Null(step.TestSummary);
        Assert.Contains("TooLarge", step.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        // The report really was bigger than the ceiling.
        var written = new FileInfo(Path.Combine(fixture.WorkspaceRoot, "big.xml"));
        Assert.True(written.Length > SafeXml.MaxReportBytes, $"the fixture wrote only {written.Length} bytes");

        // An oversized report is never stored as evidence either.
        Assert.Null(step.TestReportEvidence);
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
    }

    [Fact]
    public async Task A_dtd_bearing_report_produced_by_a_step_fails_that_step()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["report", "results.trx", "trx-doctype"],
                parser: VerificationResultParser.Trx,
                resultPath: "results.trx"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.ParseFailed, run.Steps[0].Status);
        Assert.Contains("ProhibitedXmlConstruct", run.Steps[0].Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(0, run.Steps[0].ExitCode);
        Assert.Equal(VerificationOutcome.Failed, run.RequiredOutcome);
    }

    [Fact]
    public async Task A_step_whose_report_never_appears_fails_to_parse()
    {
        using var fixture = VerificationFixture.Create();

        var profile = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step(
                "test",
                ["exit", "0"],
                parser: VerificationResultParser.JUnit,
                resultPath: "never-written.xml"));

        var run = await fixture.CreateEngine().RunAsync(profile, fixture.Request(), CancellationToken.None);

        Assert.Equal(VerificationStepStatus.ParseFailed, run.Steps[0].Status);
        Assert.Equal(0, run.Steps[0].ExitCode);
        Assert.Null(run.Steps[0].TestSummary);
    }
}
