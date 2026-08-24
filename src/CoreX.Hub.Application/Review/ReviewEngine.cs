using System.Text;
using System.Text.Json;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Review;

/// <summary>
/// The independent review of a committed result (architecture J).
///
/// It reviews a commit, never a task and never a working tree. What it is given is an
/// execution that already produced a result commit and already passed the verification that
/// was required of it; what it produces is evidence and a verdict derived from that
/// evidence. It does not implement, repair, commit, merge, push, verify or decide what the
/// default branch is, and it cannot: none of those are reachable from the ports it holds.
///
/// Three rules are worth stating before the code, because each of them is a way the review
/// could quietly become worthless.
///
/// A provider that finished is not a review. The provider can exit zero, report success and
/// return no structured output at all - measured, not imagined - and treating that as "no
/// findings" would turn every schema failure into a silent pass.
///
/// A reviewer does not decide scope. It says whether it thinks a finding is out of scope,
/// and that is evidence; the Hub decides from the paths the execution actually changed,
/// because a reviewer that owned this field could exempt its own blocking finding.
///
/// A reviewer does not decide anything else either. There is no verdict field in the
/// schema, prose is never read, and the outcome comes from ReviewFindingGate over canonical
/// findings.
/// </summary>
public sealed class ReviewEngine : IExecutionReviewer
{
    /// <summary>Frozen J.3: one retry, and only for a schema that did not arrive or did not fit.</summary>
    public const int MaximumAttempts = 2;

    private readonly IReviewWorkspace _workspace;
    private readonly IReviewRuntimeGateway _reviewer;
    private readonly IReviewEvidenceReader _evidence;
    private readonly ReviewEngineOptions _options;

    public ReviewEngine(
        IReviewWorkspace workspace,
        IReviewRuntimeGateway reviewer,
        IReviewEvidenceReader evidence,
        ReviewEngineOptions options)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _reviewer = reviewer ?? throw new ArgumentNullException(nameof(reviewer));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ExecutionReviewResult> ReviewAsync(
        ExecutionReviewRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ResultCommitSha))
        {
            return Unusable(ReviewStepFate.Failed, "there is no result commit to review");
        }

        // The diff is the anchor, and it is the one the Hub already stored. Asking the
        // reviewer to produce it would let the thing being reviewed choose what is shown.
        var diff = await _evidence.ReadAsync(request.CommitDiff, ct);

        if (string.IsNullOrWhiteSpace(diff))
        {
            return Unusable(
                ReviewStepFate.Failed,
                "the persisted commit diff could not be read, so the review had no anchor");
        }

        var snapshot = await _workspace.PrepareAsync(
            request.RepositoryId,
            request.ResultCommitSha,
            SnapshotId(request.ExecutionId),
            ct);

        if (snapshot is null)
        {
            return Unusable(ReviewStepFate.Failed, "a review snapshot could not be prepared");
        }

        ExecutionReviewResult result;

        try
        {
            result = await ReviewSnapshotAsync(request, snapshot, diff!, ct);
        }
        catch (Exception)
        {
            await _workspace.ReleaseAsync(snapshot, ct);
            throw;
        }

        // Released after the verdict is decided, and the verdict is never revisited because
        // of what happens here. A tree that will not go away is worth reporting and is not a
        // review finding: cleanup is not allowed to be a hidden judgement.
        var residual = await _workspace.ReleaseAsync(snapshot, ct);

        return residual is null ? result : result with { SnapshotResidual = residual };
    }

    private async Task<ExecutionReviewResult> ReviewSnapshotAsync(
        ExecutionReviewRequest request,
        ReviewSnapshot snapshot,
        string diff,
        CancellationToken ct)
    {
        var before = await _workspace.InspectAsync(snapshot, ct);

        if (before is null || !before.IsClean
            || !string.Equals(before.HeadSha, request.ResultCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(
                ReviewStepFate.Failed,
                "the review snapshot was not a clean tree at the result commit before the reviewer ran: "
                + (before?.Detail ?? "it could not be inspected"));
        }

        var attempts = new List<ReviewAttemptEvidence>();
        var prompt = BuildPrompt(request.ResultCommitSha, diff);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var sessionId = SessionId(request.ExecutionId, attempt);

            var run = await _reviewer.RunAsync(
                new ReviewRuntimeRequest
                {
                    WorkingDirectory = snapshot.SnapshotPath,
                    SystemPromptFile = _options.ReviewerPromptFile,
                    UserPrompt = prompt,
                    StructuredOutputSchema = ReviewSchema.Json,
                    SessionId = sessionId,
                },
                ct);

            attempts.Add(new ReviewAttemptEvidence(
                attempt, sessionId, run.Stdout, run.Stderr, run.StructuredOutput));

            // The tree is checked after every attempt, before anything the attempt said is
            // believed. A reviewer that changed the thing it was measuring has not produced
            // a review of it, and no retry can repair that.
            var after = await _workspace.InspectAsync(snapshot, ct);

            if (Tampered(before, after) is { } tamper)
            {
                return Unusable(ReviewStepFate.Failed, "ReviewerTamperDetected: " + tamper, attempts);
            }

            // Process truth first. A provider that failed, timed out, was cancelled or could
            // not do what was asked has produced nothing to validate, and none of those are
            // schema problems, so none of them is retried.
            if (Fatal(run) is { } fatal)
            {
                return Unusable(fatal.Fate, fatal.Detail, attempts);
            }

            var document = ReviewSchema.Read(run.StructuredOutput);

            if (!document.IsValid)
            {
                if (attempt < MaximumAttempts)
                {
                    continue;
                }

                return Unusable(
                    ReviewStepFate.Failed,
                    $"the reviewer returned no usable structured output in {MaximumAttempts} attempts: "
                    + document.Problem,
                    attempts);
            }

            return Judge(request, document.Document!, attempts);
        }

        // Unreachable: the loop either returns or exhausts into the branch above.
        return Unusable(ReviewStepFate.Failed, "the review produced no result", attempts);
    }

    /// <summary>
    /// Turns a valid provider document into canonical findings and a verdict.
    ///
    /// This is where the reviewer stops being an authority. Every path is validated with the
    /// Hub's own rule, every scope flag is recomputed from what the execution changed, and
    /// only then does the frozen gate get asked.
    /// </summary>
    private static ExecutionReviewResult Judge(
        ExecutionReviewRequest request,
        ReviewDocument document,
        List<ReviewAttemptEvidence> attempts)
    {
        var changed = new HashSet<string>(
            request.ChangedPaths.Select(ScopeGlob.NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var canonical = new List<CanonicalFinding>();
        var disagreements = new List<string>();

        foreach (var reported in document.Findings!)
        {
            // A path that cannot be judged is not a harmless out of scope observation. It is
            // a document this Hub cannot read, and reading it leniently would mean accepting
            // an anchor pointing outside the repository.
            if (ScopeGuard.DescribeMalformedPath(reported.File) is { } malformed)
            {
                return Unusable(
                    ReviewStepFate.Failed,
                    $"a finding names the path '{reported.File}', which cannot be judged: {malformed}",
                    attempts);
            }

            var path = ScopeGlob.NormalizePath(reported.File);
            var outOfScope = !changed.Contains(path);

            if (reported.OutOfScope != outOfScope)
            {
                // Recorded rather than hidden: a reviewer that keeps disagreeing about scope
                // is worth knowing about, and silently overwriting the field would make that
                // invisible.
                disagreements.Add(
                    $"{path}: reviewer said outOfScope={reported.OutOfScope}, changed paths say {outOfScope}");
            }

            canonical.Add(new CanonicalFinding
            {
                Severity = ReviewSchema.Severity(reported.Severity)!.Value,
                Category = ReviewSchema.Category(reported.Category)!.Value,
                Title = reported.Title!,
                Rationale = reported.Rationale!,
                File = path,
                Line = reported.Line!.Value,
                SuggestedFix = reported.SuggestedFix,
                Confidence = ReviewSchema.Confidence(reported.Confidence)!.Value,
                OutOfScope = outOfScope,
            });
        }

        var blocking = ReviewFindingGate.HasBlockingFindings(canonical.Select(Materialise));

        return new ExecutionReviewResult
        {
            Verdict = blocking ? ReviewVerdict.Blocked : ReviewVerdict.Clean,
            StepFate = ReviewStepFate.Succeeded,
            Findings = canonical,
            CanonicalDocument = Canonical(document.Summary!, canonical),
            Attempts = attempts,
            Detail = Describe(document.Summary!, canonical),
            ScopeDisagreements = disagreements,
        };
    }

    /// <summary>
    /// A domain finding purely so the frozen gate can count it.
    ///
    /// The gate takes findings, and findings belong to a persisted step that does not exist
    /// until the orchestrator writes one. These are never stored; they exist for the length
    /// of one call so that the severity arithmetic has exactly one implementation.
    /// </summary>
    private static Finding Materialise(CanonicalFinding finding) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            finding.Severity,
            finding.Category,
            finding.Title,
            finding.Rationale,
            finding.File,
            finding.Line,
            finding.SuggestedFix,
            finding.Confidence,
            finding.OutOfScope,
            DateTimeOffset.UnixEpoch);

    private static (ReviewStepFate Fate, string Detail)? Fatal(ReviewRuntimeResult run) => run.Outcome switch
    {
        "Completed" => null,
        "TimedOut" => (ReviewStepFate.TimedOut, "the reviewer ran out of time: " + (run.Diagnostic ?? "no detail")),
        "Cancelled" => (ReviewStepFate.Cancelled, "the review was cancelled"),
        _ => (ReviewStepFate.Failed, "the reviewer did not complete: " + (run.Diagnostic ?? run.Outcome)),
    };

    private static string? Tampered(ReviewSnapshotIntegrity before, ReviewSnapshotIntegrity? after)
    {
        if (after is null)
        {
            return "the snapshot could not be inspected after the reviewer ran";
        }

        if (!string.Equals(after.HeadSha, before.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return $"the snapshot moved from {Short(before.HeadSha)} to {Short(after.HeadSha)}";
        }

        if (!string.Equals(after.TreeSha, before.TreeSha, StringComparison.OrdinalIgnoreCase))
        {
            return $"the tree changed from {Short(before.TreeSha)} to {Short(after.TreeSha)}";
        }

        return after.IsClean ? null : "the snapshot is no longer clean: " + after.Detail;
    }

    /// <summary>
    /// The prompt the reviewer is given. The contract itself lives in the system prompt
    /// file; this carries only what is specific to this review.
    /// </summary>
    private static string BuildPrompt(string commitSha, string diff)
    {
        var builder = new StringBuilder();

        builder.Append("Review the result state in your working directory, which is a checkout of commit ")
            .Append(commitSha)
            .Append(".\n\nUnified diff under review:\n```diff\n")
            .Append(diff.TrimEnd())
            .Append("\n```\n");

        return builder.ToString();
    }

    private static string Canonical(string summary, IReadOnlyList<CanonicalFinding> findings) =>
        JsonSerializer.Serialize(
            new
            {
                summary,
                findings = findings.Select(f => new
                {
                    severity = f.Severity.ToString(),
                    category = f.Category.ToString(),
                    file = f.File,
                    line = f.Line,
                    title = f.Title,
                    rationale = f.Rationale,
                    suggestedFix = f.SuggestedFix,
                    confidence = f.Confidence.ToString(),
                    outOfScope = f.OutOfScope,
                }),
            },
            new JsonSerializerOptions { WriteIndented = true });

    private static string Describe(string summary, IReadOnlyList<CanonicalFinding> findings)
    {
        var inScope = findings.Count(f => !f.OutOfScope);

        return $"{findings.Count} finding(s), {inScope} in scope; "
               + ReviewFindingGate.Describe(findings.Select(Materialise))
               + (string.IsNullOrWhiteSpace(summary) ? string.Empty : "; " + Bounded(summary));
    }

    private static ExecutionReviewResult Unusable(
        ReviewStepFate fate,
        string detail,
        List<ReviewAttemptEvidence>? attempts = null) => new()
    {
        Verdict = ReviewVerdict.Unusable,
        StepFate = fate,
        Findings = [],
        Attempts = attempts ?? [],
        Detail = detail,
    };

    /// <summary>
    /// Deterministic, and different for each attempt.
    ///
    /// Two invocations that shared a session identifier would be indistinguishable in the
    /// provider's own records, which is exactly what a retry must not be. Derived rather
    /// than random so an execution's reviewer sessions can be named again later without
    /// storing them anywhere.
    /// </summary>
    public static string SessionId(Guid executionId, int attempt) =>
        Catalog.DeterministicGuid
            .Create(attempt <= 1 ? "review" : "review-retry", executionId.ToString("D"))
            .ToString("D");

    private static string SnapshotId(Guid executionId) => "review-" + executionId.ToString("N");

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(12, sha.Length)];

    private static string Bounded(string text) => text.Length <= 300 ? text : text[..300];
}

/// <summary>Where the Hub owned reviewer contract lives. Not a settings framework.</summary>
public sealed record ReviewEngineOptions
{
    /// <summary>Absolute path to the Hub authored reviewer system prompt.</summary>
    public required string ReviewerPromptFile { get; init; }
}
