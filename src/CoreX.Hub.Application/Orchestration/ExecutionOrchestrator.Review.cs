using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// The independent review of a result that already committed and already verified.
    ///
    /// The orchestrator owns the sequencing and the writing; the reviewer owns the outside
    /// work and returns facts. That split is deliberate: findings, the review evidence and
    /// the step's fate are written here, under the same transaction discipline as every
    /// other step, so an execution can never end as ReviewBlocked while the findings that
    /// blocked it live only in a returned object.
    ///
    /// Nothing here reads reviewer prose, counts severities or decides scope. Those answers
    /// arrive already canonical, because the alternative - a second severity rule living in
    /// the orchestrator - is how two authorities start disagreeing.
    /// </summary>
    private async Task<ExecutionRunReport> ReviewAndFinishAsync(
        Execution execution,
        HubTask task,
        Guid repositoryId,
        string resultCommitSha,
        IReadOnlyList<string> changedPaths,
        EvidenceArtifact? commitDiff,
        string? verificationResidual,
        CancellationToken ct,
        CancellationToken record)
    {
        var step = await FindStepAsync(execution.Id, ExecutionStepKind.Review, record);

        // The review is anchored to the diff the Hub itself stored. Without it there is
        // nothing to review against, and asking the reviewer to produce its own anchor would
        // let the thing under review choose what is shown.
        if (commitDiff is null)
        {
            await FinishStepAsync(
                execution.Id, ExecutionStepKind.Review, StepFate.Failed, NoCommitDiffEvidence, record);

            return await CompleteAsync(
                execution,
                task,
                ExecutionOutcome.ReviewUnusable,
                "no commit diff evidence was stored, so the review had nothing to anchor to",
                verificationResidual,
                record);
        }

        ExecutionReviewResult review;

        try
        {
            review = await _reviewer!.ReviewAsync(
                new ExecutionReviewRequest
                {
                    ExecutionId = execution.Id,
                    RepositoryId = repositoryId,
                    ResultCommitSha = resultCommitSha,
                    ChangedPaths = changedPaths,
                    CommitDiff = commitDiff,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A review the caller stopped is not a review that found nothing, and it is not
            // a cancelled execution either: the result commit exists and is never disowned.
            // ReviewUnusable says exactly what is true - no usable review was obtained.
            review = new ExecutionReviewResult
            {
                Verdict = ReviewVerdict.Unusable,
                StepFate = ReviewStepFate.Cancelled,
                Detail = "the review was cancelled before it produced a usable result",
            };
        }

        await WriteReviewEvidenceAsync(execution, step?.Id, review, record);

        if (step is not null && review.Findings.Count > 0)
        {
            await PersistFindingsAsync(execution, step, review.Findings, record);
        }

        foreach (var disagreement in review.ScopeDisagreements)
        {
            // The Hub's answer already won. This exists so a reviewer that keeps claiming a
            // different scope is visible rather than silently corrected.
            await RecordEventAsync(execution, "ReviewScopeDisagreement", disagreement, record);
        }

        if (review.SnapshotResidual is { } residual)
        {
            await RecordEventAsync(execution, "SnapshotResidual", residual, record);
        }

        await RecordEventAsync(execution, "Review" + review.Verdict, review.Detail, record);

        await FinishStepAsync(
            execution.Id, ExecutionStepKind.Review, StepFateFor(review.StepFate), review.Detail, record);

        return await CompleteAsync(
            execution,
            task,
            OutcomeFor(review.Verdict),
            review.Detail,
            // Two disposable trees can each leave something behind. Both are reported, and
            // neither changes what was measured or what the review found.
            Join(verificationResidual, review.SnapshotResidual),
            record);
    }

    /// <summary>
    /// Stores what each reviewer attempt produced, scoped to the Review step.
    ///
    /// The kinds are the same ones the implementer's output uses, and that is the point:
    /// they are the same kinds of thing. What separates them is the step they belong to, so
    /// a reader asking what the reviewer said gets the reviewer's transcript and never the
    /// implementer's.
    /// </summary>
    private async Task WriteReviewEvidenceAsync(
        Execution execution,
        Guid? stepId,
        ExecutionReviewResult review,
        CancellationToken ct)
    {
        foreach (var attempt in review.Attempts)
        {
            await WriteEvidenceAsync(
                execution, attempt.Stdout, EvidenceKind.AgentStdout, "text/plain", ct, stepId);
            await WriteEvidenceAsync(
                execution, attempt.Stderr, EvidenceKind.AgentStderr, "text/plain", ct, stepId);

            if (attempt.StructuredOutput is { Length: > 0 } structured)
            {
                await WriteEvidenceAsync(
                    execution, structured, EvidenceKind.AgentStructuredResult, "application/json", ct, stepId);
            }

            await RecordEventAsync(
                execution,
                "ReviewAttempt",
                $"attempt {attempt.AttemptNumber} as session {attempt.SessionId}, "
                + (attempt.StructuredOutput is { Length: > 0 }
                    ? "structured output present"
                    : "no structured output"),
                ct);
        }

        // The canonical document, not the provider's. It is what the Hub decided the review
        // says after validating every field, and it is the copy a later reader should trust.
        if (review.CanonicalDocument is { Length: > 0 } canonical)
        {
            await WriteEvidenceAsync(
                execution, canonical, EvidenceKind.ReviewFindings, "application/json", ct, stepId);
        }
    }

    /// <summary>Writes the findings as one transaction. A partly written review is not a review.</summary>
    private async Task PersistFindingsAsync(
        Execution execution,
        ExecutionStep step,
        IReadOnlyList<CanonicalFinding> findings,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        foreach (var finding in findings)
        {
            _store.AddFinding(new Finding(
                Guid.CreateVersion7(),
                execution.OrganizationId,
                step.Id,
                finding.Severity,
                finding.Category,
                finding.Title,
                finding.Rationale,
                finding.File,
                finding.Line,
                finding.SuggestedFix,
                finding.Confidence,
                finding.OutOfScope,
                now));
        }

        await _store.SaveChangesAsync(ct);
    }

    private const string NoCommitDiffEvidence = "NoCommitDiffEvidence";

    private const string ReviewSkippedVerificationFailed = "VerificationDidNotPass";

    /// <summary>
    /// The review's verdict, in the execution's vocabulary.
    ///
    /// Blocked is a review that worked. Unusable is the absence of one, and it is not a pass:
    /// an execution nobody could review is not an execution somebody approved.
    /// </summary>
    private static ExecutionOutcome OutcomeFor(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Clean => ExecutionOutcome.Passed,
        ReviewVerdict.Blocked => ExecutionOutcome.ReviewBlocked,
        ReviewVerdict.Unusable => ExecutionOutcome.ReviewUnusable,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "unknown review verdict"),
    };

    /// <summary>
    /// A review that found a blocker did its job, so it is a step that succeeded. Only the
    /// absence of a review is a step that did not.
    /// </summary>
    private static StepFate StepFateFor(ReviewStepFate fate) => fate switch
    {
        ReviewStepFate.Succeeded => StepFate.Succeeded,
        ReviewStepFate.TimedOut => StepFate.TimedOut,
        ReviewStepFate.Cancelled => StepFate.Cancelled,
        ReviewStepFate.Unsupported => StepFate.Unsupported,
        _ => StepFate.Failed,
    };

    private static string? Join(string? first, string? second) => (first, second) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } only, null) => only,
        var (a, b) => a + "; " + b,
    };
}
