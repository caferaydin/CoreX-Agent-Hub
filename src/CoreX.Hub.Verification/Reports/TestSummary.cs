using System.Globalization;

namespace CoreX.Hub.Verification.Reports;

/// <summary>Which report dialect produced a summary.</summary>
public enum TestReportFormat
{
    Trx,
    JUnit,
}

/// <summary>
/// Structured counters extracted from a machine readable test report.
///
/// Deliberately independent from console wording: nothing here is scraped from the
/// locale formatted summary line a test runner prints (Phase 4 brief section 16).
/// </summary>
public sealed record TestSummary
{
    public required TestReportFormat Format { get; init; }

    public required int Total { get; init; }

    public required int Passed { get; init; }

    public required int Failed { get; init; }

    public required int Errors { get; init; }

    public required int Skipped { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Set when the report parsed but something in it needed reporting.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>Failing in the sense that matters to a gate: a failure or an error.</summary>
    public int FailedOrErrored => Failed + Errors;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Format}: {Total} total, {Passed} passed, {Failed} failed, {Errors} errors, {Skipped} skipped");
}

/// <summary>Why a report could not be turned into counters.</summary>
public enum ReportParseFailure
{
    ReportMissing,
    ReportTooLarge,
    MalformedXml,
    ProhibitedXmlConstruct,
    UnexpectedStructure,
}

public sealed record ReportParseResult
{
    public TestSummary? Summary { get; init; }

    public ReportParseFailure? Failure { get; init; }

    public string? Detail { get; init; }

    public bool Succeeded => Summary is not null && Failure is null;

    public static ReportParseResult Ok(TestSummary summary) => new() { Summary = summary };

    public static ReportParseResult Failed(ReportParseFailure failure, string detail) =>
        new() { Failure = failure, Detail = detail };

    public string Describe() =>
        Succeeded ? Summary!.Describe() : $"{Failure}: {Detail}";
}
