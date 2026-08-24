using System.Text.Json;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A review workspace that answers from a script rather than from git.
///
/// The real one is proved against real repositories by the workspace tests. What the review
/// engine needs proving about is what it does with the answers - a tree that moved, a tree
/// that went dirty, a tree nobody could inspect - and those are states that are awkward to
/// produce for real and trivial to state here.
/// </summary>
public sealed class FakeReviewWorkspace : IReviewWorkspace
{
    private readonly Queue<ReviewSnapshotIntegrity?> _inspections = new();

    public FakeReviewWorkspace(string commitSha = Head)
    {
        CommitSha = commitSha;
    }

    public const string Head = "1111111111111111111111111111111111111111";

    public const string Tree = "2222222222222222222222222222222222222222";

    public string CommitSha { get; }

    /// <summary>Null makes preparation fail, which is a review that never started.</summary>
    public string? SnapshotPath { get; set; } = @"C:\cx\_review\review-1";

    public string? Residual { get; set; }

    public List<string> Prepared { get; } = [];

    public int Released { get; private set; }

    /// <summary>Adds one answer to the sequence of inspections, in the order they are asked for.</summary>
    public FakeReviewWorkspace Inspecting(ReviewSnapshotIntegrity? integrity)
    {
        _inspections.Enqueue(integrity);
        return this;
    }

    public FakeReviewWorkspace InspectingClean(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Inspecting(new ReviewSnapshotIntegrity(CommitSha, Tree, true, "clean"));
        }

        return this;
    }

    public Task<ReviewSnapshot?> PrepareAsync(
        Guid repositoryId,
        string commitSha,
        string snapshotId,
        CancellationToken ct)
    {
        Prepared.Add(snapshotId);

        return Task.FromResult(SnapshotPath is null ? null : new ReviewSnapshot(SnapshotPath, commitSha));
    }

    public Task<ReviewSnapshotIntegrity?> InspectAsync(ReviewSnapshot snapshot, CancellationToken ct) =>
        Task.FromResult(_inspections.Count > 0
            ? _inspections.Dequeue()
            : new ReviewSnapshotIntegrity(CommitSha, Tree, true, "clean"));

    public Task<string?> ReleaseAsync(ReviewSnapshot snapshot, CancellationToken ct)
    {
        Released++;
        return Task.FromResult(Residual);
    }
}

/// <summary>A reviewer that returns exactly what a test says it returns, one answer per attempt.</summary>
public sealed class FakeReviewRuntime : IReviewRuntimeGateway
{
    private readonly Queue<ReviewRuntimeResult> _answers = new();

    public List<ReviewRuntimeRequest> Requests { get; } = [];

    public FakeReviewRuntime Returning(ReviewRuntimeResult result)
    {
        _answers.Enqueue(result);
        return this;
    }

    public FakeReviewRuntime Returning(string? structuredOutput) =>
        Returning(new ReviewRuntimeResult { Outcome = "Completed", StructuredOutput = structuredOutput });

    public FakeReviewRuntime Failing(string outcome, string diagnostic = "measured") =>
        Returning(new ReviewRuntimeResult { Outcome = outcome, Diagnostic = diagnostic });

    public Task<ReviewRuntimeResult> RunAsync(ReviewRuntimeRequest request, CancellationToken ct)
    {
        Requests.Add(request);

        return Task.FromResult(_answers.Count > 0
            ? _answers.Dequeue()
            : new ReviewRuntimeResult { Outcome = "Failed", Diagnostic = "the test scripted no further answer" });
    }
}

/// <summary>Hands back a stored diff without a store.</summary>
public sealed class FakeReviewEvidence : IReviewEvidenceReader
{
    public string? Diff { get; set; } = "--- a/src/One.cs\n+++ b/src/One.cs\n@@\n+changed\n";

    public Task<string?> ReadAsync(EvidenceArtifact artifact, CancellationToken ct) => Task.FromResult(Diff);
}

/// <summary>Builds provider documents the way a provider would, so tests state findings, not json.</summary>
public static class ReviewDocuments
{
    public static string Document(string summary, params object[] findings) =>
        JsonSerializer.Serialize(new { findings, summary });

    public static object Finding(
        string severity = "Major",
        string file = "src/One.cs",
        int line = 12,
        bool outOfScope = false,
        string category = "correctness",
        string confidence = "high",
        string title = "A finding",
        string rationale = "Because the code says so.",
        string? suggestedFix = null) =>
        new
        {
            severity,
            category,
            file,
            line,
            title,
            rationale,
            suggestedFix,
            confidence,
            outOfScope,
        };

    public static EvidenceArtifact CommitDiff(Guid executionId) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            executionId,
            null,
            EvidenceKind.GitCommitDiff,
            new string('a', 64),
            128,
            "text/plain",
            0,
            DateTimeOffset.UnixEpoch);

    public static ExecutionReviewRequest Request(
        Guid executionId,
        IReadOnlyList<string>? changedPaths = null,
        string commitSha = FakeReviewWorkspace.Head) =>
        new()
        {
            ExecutionId = executionId,
            RepositoryId = Guid.CreateVersion7(),
            ResultCommitSha = commitSha,
            ChangedPaths = changedPaths ?? ["src/One.cs"],
            CommitDiff = CommitDiff(executionId),
        };

    public static ReviewEngine Engine(
        FakeReviewWorkspace workspace,
        FakeReviewRuntime runtime,
        FakeReviewEvidence? evidence = null) =>
        new(
            workspace,
            runtime,
            evidence ?? new FakeReviewEvidence(),
            new ReviewEngineOptions { ReviewerPromptFile = @"C:\hub\review\reviewer.md" });
}
