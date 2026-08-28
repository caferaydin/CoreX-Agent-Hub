using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Review;

/// <summary>A disposable working tree pinned to one exact commit, for reviewing it.</summary>
public sealed record ReviewSnapshot(string SnapshotPath, string CommitSha);

/// <summary>
/// What the snapshot looked like at one moment, in neutral terms.
///
/// Deliberately not a git result type. The Application never learns what a tree hash is
/// made of or which command produced it; it only compares two of these and says whether
/// they are the same.
/// </summary>
public sealed record ReviewSnapshotIntegrity(string HeadSha, string TreeSha, bool IsClean, string Detail);

/// <summary>
/// The Application owned port for the review working tree.
///
/// Every git command behind it belongs to the workspace module (E.1). The engine asks for a
/// tree at a commit, asks what state it is in, and asks for it to go away; it never builds
/// an argument vector, and there is deliberately no "run a git command" method here for it
/// to reach for.
/// </summary>
public interface IReviewWorkspace
{
    /// <param name="executionId">
    /// The execution whose result is being reviewed, already durable when this is called.
    ///
    /// Carried rather than derived from the snapshot name. An isolated organization resolves the
    /// boundary its review tree and reviewer belong in from this identity, and re-deriving it by
    /// parsing a string the Hub composed for a directory would make a filesystem name into resource
    /// authority. Execution vocabulary only - no isolation concept crosses this contract.
    /// </param>
    Task<ReviewSnapshot?> PrepareAsync(
        Guid executionId,
        Guid repositoryId,
        string commitSha,
        string snapshotId,
        CancellationToken ct);

    /// <summary>Null when the snapshot could not be inspected at all, which is itself a refusal.</summary>
    Task<ReviewSnapshotIntegrity?> InspectAsync(ReviewSnapshot snapshot, CancellationToken ct);

    /// <summary>Returns a residual description when the tree would not go away, else null.</summary>
    Task<string?> ReleaseAsync(ReviewSnapshot snapshot, CancellationToken ct);
}

/// <summary>Reads back a persisted evidence blob. Redaction already happened on the way in.</summary>
public interface IReviewEvidenceReader
{
    Task<string?> ReadAsync(EvidenceArtifact artifact, CancellationToken ct);
}

/// <summary>What one reviewer invocation is asked to do.</summary>
public sealed record ReviewRuntimeRequest
{
    public required string WorkingDirectory { get; init; }

    public required string SystemPromptFile { get; init; }

    public required string UserPrompt { get; init; }

    public required string StructuredOutputSchema { get; init; }

    /// <summary>Deterministic, derived from the execution, and never the implementer's.</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The execution being reviewed.
    ///
    /// The session identity is derived from it and is not a substitute for it: one is a string a
    /// provider is told, the other is what an isolated reviewer's boundary is resolved from, and a
    /// gateway that parsed the first back into the second would be accepting correlation text as
    /// resource authority.
    /// </summary>
    public required Guid ExecutionId { get; init; }
}

/// <summary>Machine truth about one reviewer invocation, provider neutral.</summary>
public sealed record ReviewRuntimeResult
{
    /// <summary>The runtime's own outcome vocabulary, as a string the Application can map.</summary>
    public required string Outcome { get; init; }

    /// <summary>The provider's structured document, or null when it produced none.</summary>
    public string? StructuredOutput { get; init; }

    public string Stdout { get; init; } = string.Empty;

    public string Stderr { get; init; } = string.Empty;

    public string? ProviderSessionId { get; init; }

    public string? Diagnostic { get; init; }
}

/// <summary>
/// The Application owned port for running a reviewer.
///
/// Which runtime this is was decided by composition from the reviewer agent profile, the
/// same way the implementer's was. Nothing above this line learns the answer, which is what
/// lets a later phase point the reviewer at a different provider by changing one profile.
/// </summary>
public interface IReviewRuntimeGateway
{
    Task<ReviewRuntimeResult> RunAsync(ReviewRuntimeRequest request, CancellationToken ct);
}

/// <summary>What the orchestrator hands the review engine.</summary>
public sealed record ExecutionReviewRequest
{
    public required Guid ExecutionId { get; init; }

    public required Guid RepositoryId { get; init; }

    /// <summary>Required. A review of an execution that produced no commit reviews nothing.</summary>
    public required string ResultCommitSha { get; init; }

    /// <summary>The paths this execution actually changed. Machine truth, not reviewer claim.</summary>
    public required IReadOnlyList<string> ChangedPaths { get; init; }

    /// <summary>The persisted commit diff artifact the review is anchored to.</summary>
    public required EvidenceArtifact CommitDiff { get; init; }
}

/// <summary>How a review ended, in the vocabulary the orchestrator already speaks.</summary>
public enum ReviewVerdict
{
    /// <summary>The reviewer worked and found nothing that blocks.</summary>
    Clean,

    /// <summary>The reviewer worked and found something that blocks. Not a failed review.</summary>
    Blocked,

    /// <summary>No usable review exists, whatever the provider said about itself.</summary>
    Unusable,
}

/// <summary>One canonical finding, after Hub validation and Hub scope derivation.</summary>
public sealed record CanonicalFinding
{
    public required FindingSeverity Severity { get; init; }

    public required FindingCategory Category { get; init; }

    public required string Title { get; init; }

    public required string Rationale { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public string? SuggestedFix { get; init; }

    public required FindingConfidence Confidence { get; init; }

    /// <summary>Derived from the changed path set. The reviewer's own claim is not used here.</summary>
    public required bool OutOfScope { get; init; }
}

/// <summary>Raw output from one reviewer attempt, kept whatever the attempt produced.</summary>
public sealed record ReviewAttemptEvidence(
    int AttemptNumber,
    string SessionId,
    string Stdout,
    string Stderr,
    string? StructuredOutput);

/// <summary>
/// Everything one review produced, for the orchestrator to persist and act on.
///
/// The engine does the outside work and returns facts; writing them down is the
/// orchestrator's job, because it owns the transaction that also closes the step, the
/// execution and the task. Splitting it that way is what makes "ReviewBlocked with no
/// persisted findings" impossible rather than merely unlikely.
/// </summary>
public sealed record ExecutionReviewResult
{
    public required ReviewVerdict Verdict { get; init; }

    /// <summary>How the Review step itself ended, which is not the same as the verdict.</summary>
    public required ReviewStepFate StepFate { get; init; }

    public IReadOnlyList<CanonicalFinding> Findings { get; init; } = [];

    /// <summary>The canonical document persisted as ReviewFindings evidence.</summary>
    public string? CanonicalDocument { get; init; }

    public IReadOnlyList<ReviewAttemptEvidence> Attempts { get; init; } = [];

    public required string Detail { get; init; }

    /// <summary>Set when a disposable snapshot would not go away. Never changes the verdict.</summary>
    public string? SnapshotResidual { get; init; }

    /// <summary>Recorded when the reviewer's own scope claim disagreed with the Hub's.</summary>
    public IReadOnlyList<string> ScopeDisagreements { get; init; } = [];
}

/// <summary>
/// How the Review step ended.
///
/// Separate from the verdict on purpose. A review that found a blocker succeeded - it did
/// exactly its job - so Blocked maps to a Succeeded step, and only a review that could not
/// be obtained maps to anything else.
/// </summary>
public enum ReviewStepFate
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Unsupported,
}

/// <summary>The Application owned port for reviewing a committed result.</summary>
public interface IExecutionReviewer
{
    Task<ExecutionReviewResult> ReviewAsync(ExecutionReviewRequest request, CancellationToken ct);
}
