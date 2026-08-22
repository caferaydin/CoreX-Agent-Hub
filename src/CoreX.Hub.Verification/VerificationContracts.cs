using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification;

/// <summary>
/// How one verification step ended.
///
/// Deliberately NOT an execution outcome: mapping this onto Execution.Outcome, or onto an
/// acceptance decision, belongs to the orchestration phase.
/// </summary>
public enum VerificationStepStatus
{
    /// <summary>Process exited with code 0, and any configured parser succeeded.</summary>
    Passed,

    /// <summary>Process ran and exited with a non zero code.</summary>
    Failed,

    /// <summary>The step timeout elapsed and the process tree was terminated.</summary>
    TimedOut,

    /// <summary>The caller cancelled the run.</summary>
    Cancelled,

    /// <summary>Nothing was launched: rejected executable, rejected path, or a start failure.</summary>
    StartFailed,

    /// <summary>The command succeeded but its report could not be turned into counters.</summary>
    ParseFailed,

    /// <summary>Deterministically not run because a required step already failed.</summary>
    Skipped,
}

/// <summary>Whether the required part of a profile held.</summary>
public enum VerificationOutcome
{
    Passed,
    Failed,
}

/// <summary>What the caller asks the engine to verify.</summary>
public sealed record VerificationRequest
{
    /// <summary>
    /// Trust root for every path this run touches. The profile working directory and any
    /// report path resolve under it, and nothing may escape it.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Environment variable names copied from the parent process; nothing else is visible.</summary>
    public IReadOnlyList<string> EnvironmentAllowlist { get; init; } = [];

    /// <summary>Explicit values layered on top of the allowlist.</summary>
    public IReadOnlyDictionary<string, string> AdditionalEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Short label used in evidence text; never part of the profile hash.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>When false, evidence blobs are not written. Used by baseline collection callers.</summary>
    public bool CollectEvidence { get; init; } = true;
}

/// <summary>Machine truth for one verification step.</summary>
public sealed record VerificationStepResult
{
    public required string StepName { get; init; }

    public required bool Required { get; init; }

    public required VerificationStepStatus Status { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }

    public TimeSpan Duration =>
        StartedAtUtc is { } from && EndedAtUtc is { } to && to >= from ? to - from : TimeSpan.Zero;

    /// <summary>The absolute path the logical executable name resolved to, when it did.</summary>
    public string? ResolvedExecutable { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public VerificationTerminationReason? TerminationReason { get; init; }

    public int? ExitCode { get; init; }

    public TestSummary? TestSummary { get; init; }

    public EvidenceRef? StdoutEvidence { get; init; }

    public EvidenceRef? StderrEvidence { get; init; }

    public EvidenceRef? TestReportEvidence { get; init; }

    /// <summary>Why the step ended the way it did, when the status alone is not enough.</summary>
    public string? Diagnostic { get; init; }

    public bool Succeeded => Status == VerificationStepStatus.Passed;
}

/// <summary>Machine truth for one whole verification run.</summary>
public sealed record VerificationRunResult
{
    public required string ProfileName { get; init; }

    public required string ProfileHash { get; init; }

    public required IReadOnlyList<VerificationStepResult> Steps { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    /// <summary>Passed only when every required step passed. Optional failures never change it.</summary>
    public VerificationOutcome RequiredOutcome =>
        Steps.Any(s => s.Required && s.Status != VerificationStepStatus.Passed)
            ? VerificationOutcome.Failed
            : VerificationOutcome.Passed;

    /// <summary>An optional step failed. Visible, reported, and not fatal.</summary>
    public bool HasOptionalFailures =>
        Steps.Any(s => !s.Required && s.Status != VerificationStepStatus.Passed
                       && s.Status != VerificationStepStatus.Skipped);

    /// <summary>Counters from the last parser enabled step that produced them.</summary>
    public TestSummary? TestSummary =>
        Steps.LastOrDefault(s => s.TestSummary is not null)?.TestSummary;

    /// <summary>Set only when a baseline was supplied for comparison.</summary>
    public BaselineComparisonResult? BaselineComparison { get; init; }

    public string Describe()
    {
        var steps = string.Join(", ", Steps.Select(s => $"{s.StepName}={s.Status}"));
        return $"{ProfileName} [{RequiredOutcome}] {steps}";
    }
}
