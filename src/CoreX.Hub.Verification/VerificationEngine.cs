using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Verification;

/// <summary>
/// Runs a verification profile: sequentially, in declared order, with no dependency graph
/// and no parallelism.
///
/// This is a low level primitive. It does not decide whether verification is allowed to
/// happen - the K0..K3 gates and the ResultCommitSha requirement belong to the
/// orchestrator (Phase 4 brief section 11). A caller invokes it, and it verifies.
/// </summary>
public sealed class VerificationEngine
{
    private const string TextMediaType = "text/plain; charset=utf-8";
    private const string XmlMediaType = "application/xml";

    private readonly IVerificationCommandExecutor _executor;
    private readonly IVerificationFileReader _files;
    private readonly IVerificationEvidenceSink _evidence;
    private readonly ExecutableAllowlist _allowlist;
    private readonly TimeProvider _clock;

    public VerificationEngine(
        IVerificationCommandExecutor executor,
        IVerificationFileReader files,
        IVerificationEvidenceSink evidence,
        ExecutableAllowlist allowlist,
        TimeProvider? clock = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<VerificationRunResult> RunAsync(
        VerificationProfile profile,
        VerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _clock.GetUtcNow();
        var profileHash = ProfileHasher.Compute(profile);
        var results = new List<VerificationStepResult>(profile.Steps.Count);

        var workingDirectory = _files.ResolveUnderRoot(
            request.WorkspaceRoot, profile.WorkingDirectory, mustExist: true);

        // A rejected working directory is a property of the run, not of one step, so every
        // step reports the same StartFailed rather than the first one swallowing it.
        if (!workingDirectory.IsAllowed)
        {
            foreach (var rejected in profile.Steps)
            {
                results.Add(new VerificationStepResult
                {
                    StepName = rejected.Name,
                    Required = rejected.Required,
                    Status = VerificationStepStatus.StartFailed,
                    Arguments = rejected.Args,
                    Diagnostic = "verification working directory was rejected: " + workingDirectory.Reason,
                });
            }

            return Finish(profile, profileHash, results, startedAt);
        }

        var stopBecauseRequiredFailed = false;

        foreach (var step in profile.Steps)
        {
            if (stopBecauseRequiredFailed)
            {
                results.Add(Skipped(step, "a required step already failed"));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Skipped(step, "the run was cancelled"));
                continue;
            }

            var result = await RunStepAsync(
                step, workingDirectory.CanonicalPath!, request, cancellationToken);

            results.Add(result);

            // Fail fast: once a required step fails, later steps cannot be interpreted, so
            // they are deterministically skipped instead of run against a broken tree.
            if (step.Required && result.Status != VerificationStepStatus.Passed)
            {
                stopBecauseRequiredFailed = true;
            }
        }

        return Finish(profile, profileHash, results, startedAt);
    }

    private async Task<VerificationStepResult> RunStepAsync(
        VerificationStep step,
        string workingDirectory,
        VerificationRequest request,
        CancellationToken cancellationToken)
    {
        var executable = _allowlist.Resolve(step.Executable);
        if (!executable.IsAllowed)
        {
            // Fail closed before anything is launched.
            return new VerificationStepResult
            {
                StepName = step.Name,
                Required = step.Required,
                Status = VerificationStepStatus.StartFailed,
                Arguments = step.Args,
                WorkingDirectory = workingDirectory,
                Diagnostic = executable.Reason,
            };
        }

        string? reportPath = null;
        if (step.ResultParser != VerificationResultParser.None)
        {
            var resolved = _files.ResolveUnderRoot(workingDirectory, step.ResultPath!, mustExist: false);
            if (!resolved.IsAllowed)
            {
                return new VerificationStepResult
                {
                    StepName = step.Name,
                    Required = step.Required,
                    Status = VerificationStepStatus.StartFailed,
                    Arguments = step.Args,
                    ResolvedExecutable = executable.AbsolutePath,
                    WorkingDirectory = workingDirectory,
                    Diagnostic = "result path was rejected: " + resolved.Reason,
                };
            }

            reportPath = resolved.CanonicalPath;
        }

        var command = new VerificationCommand
        {
            Executable = executable.AbsolutePath!,
            Arguments = step.Args,
            WorkingDirectory = workingDirectory,
            EnvironmentAllowlist = request.EnvironmentAllowlist,
            Environment = request.AdditionalEnvironment,
            Timeout = TimeSpan.FromSeconds(step.TimeoutSeconds),
        };

        var outcome = await _executor.ExecuteAsync(command, cancellationToken);

        var stdout = await WriteEvidenceAsync(
            request, outcome.Stdout, EvidenceKind.CommandStdout, TextMediaType, cancellationToken);
        var stderr = await WriteEvidenceAsync(
            request, outcome.Stderr, EvidenceKind.CommandStderr, TextMediaType, cancellationToken);

        var status = MapStatus(outcome);
        var diagnostic = outcome.StartFailureMessage;

        TestSummary? summary = null;
        EvidenceRef? report = null;

        // A step that never started has no report to read, and a failing step may still
        // have written one - the counters are useful evidence either way.
        if (step.ResultParser != VerificationResultParser.None
            && status is not VerificationStepStatus.StartFailed and not VerificationStepStatus.Cancelled)
        {
            var parsed = await ParseReportAsync(step, reportPath!, request, cancellationToken);
            summary = parsed.Summary;
            report = parsed.Evidence;

            if (parsed.Summary is null)
            {
                status = VerificationStepStatus.ParseFailed;
                diagnostic = Join(diagnostic, parsed.Diagnostic);
            }
            else if (parsed.Summary.Diagnostic is not null)
            {
                diagnostic = Join(diagnostic, parsed.Summary.Diagnostic);
            }
        }

        if (outcome.StdoutTruncated || outcome.StderrTruncated)
        {
            diagnostic = Join(diagnostic, "captured output was truncated at the capture ceiling");
        }

        return new VerificationStepResult
        {
            StepName = step.Name,
            Required = step.Required,
            Status = status,
            StartedAtUtc = outcome.StartedAtUtc,
            EndedAtUtc = outcome.EndedAtUtc,
            ResolvedExecutable = executable.AbsolutePath,
            Arguments = step.Args,
            WorkingDirectory = workingDirectory,
            TerminationReason = outcome.TerminationReason,
            ExitCode = outcome.ExitCode,
            TestSummary = summary,
            StdoutEvidence = stdout,
            StderrEvidence = stderr,
            TestReportEvidence = report,
            Diagnostic = diagnostic,
        };
    }

    /// <summary>
    /// Reads, parses, then stores. The order matters: the counters come from the raw report
    /// so redaction can never change them, and only the redacted bytes are ever persisted
    /// (Phase 4 brief section 35).
    /// </summary>
    private async Task<(TestSummary? Summary, EvidenceRef? Evidence, string? Diagnostic)> ParseReportAsync(
        VerificationStep step,
        string reportPath,
        VerificationRequest request,
        CancellationToken cancellationToken)
    {
        // Revalidated as an existing path: the build just created it, and it could have
        // been created as a link pointing somewhere else.
        var directory = Path.GetDirectoryName(reportPath) ?? reportPath;
        var existing = _files.ResolveUnderRoot(directory, Path.GetFileName(reportPath), mustExist: true);

        if (!existing.IsAllowed)
        {
            return (null, null, "the report was not readable: " + existing.Reason);
        }

        var read = await _files.ReadTextAsync(existing.CanonicalPath!, SafeXml.MaxReportBytes, cancellationToken);
        if (!read.Succeeded)
        {
            return (null, null, $"the report could not be read ({read.Failure}): {read.Detail}");
        }

        var parsed = step.ResultParser switch
        {
            VerificationResultParser.Trx => TrxReportParser.Parse(read.Text!),
            VerificationResultParser.JUnit => JUnitReportParser.Parse(read.Text!),
            _ => ReportParseResult.Failed(
                ReportParseFailure.UnexpectedStructure, "no parser is implemented for " + step.ResultParser),
        };

        var evidence = await WriteEvidenceAsync(
            request, read.Text!, EvidenceKind.TestReport, XmlMediaType, cancellationToken);

        return parsed.Succeeded
            ? (parsed.Summary, evidence, null)
            : (null, evidence, parsed.Describe());
    }

    private async Task<EvidenceRef?> WriteEvidenceAsync(
        VerificationRequest request,
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (!request.CollectEvidence || string.IsNullOrEmpty(text))
        {
            return null;
        }

        return await _evidence.WriteTextAsync(text, kind, mediaType, cancellationToken);
    }

    private static VerificationStepStatus MapStatus(VerificationCommandResult result) =>
        result.TerminationReason switch
        {
            VerificationTerminationReason.StartFailed => VerificationStepStatus.StartFailed,
            VerificationTerminationReason.Cancelled => VerificationStepStatus.Cancelled,
            VerificationTerminationReason.TimedOut => VerificationStepStatus.TimedOut,
            _ => result.ExitCode == 0 ? VerificationStepStatus.Passed : VerificationStepStatus.Failed,
        };

    private static VerificationStepResult Skipped(VerificationStep step, string why) => new()
    {
        StepName = step.Name,
        Required = step.Required,
        Status = VerificationStepStatus.Skipped,
        Arguments = step.Args,
        Diagnostic = why,
    };

    private VerificationRunResult Finish(
        VerificationProfile profile,
        string profileHash,
        List<VerificationStepResult> steps,
        DateTimeOffset startedAt) => new()
    {
        ProfileName = profile.Name,
        ProfileHash = profileHash,
        Steps = steps,
        StartedAtUtc = startedAt,
        EndedAtUtc = _clock.GetUtcNow(),
    };

    private static string? Join(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right
        : string.IsNullOrWhiteSpace(right) ? left
        : left + "; " + right;
}
