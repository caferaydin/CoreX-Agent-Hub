using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// Stores what the runtime said. Its claims are evidence about the runtime, never
    /// evidence about the work: nothing downstream reads them to decide an outcome.
    /// </summary>
    private async Task WriteAgentEvidenceAsync(
        Execution execution,
        AgentExecutionResult result,
        CancellationToken ct)
    {
        await WriteEvidenceAsync(execution, result.Stdout, EvidenceKind.AgentStdout, "text/plain", ct);
        await WriteEvidenceAsync(execution, result.Stderr, EvidenceKind.AgentStderr, "text/plain", ct);

        if (result.StructuredOutput is { Length: > 0 } structured)
        {
            await WriteEvidenceAsync(
                execution, structured, EvidenceKind.AgentStructuredResult, "application/json", ct);
        }

        await RecordEventAsync(
            execution,
            "AgentFinished",
            $"{result.Outcome} exit={result.ExitCode?.ToString() ?? "none"} in {result.Duration.TotalSeconds:F1}s",
            ct);
    }

    /// <summary>
    /// Stores the machine truth about the working tree. The diff goes through redaction on
    /// the way in, so a secret the agent wrote is blocked by K3 from memory and never
    /// reaches storage in the clear.
    /// </summary>
    private async Task WriteCaptureEvidenceAsync(
        Execution execution,
        ExecutionCapture capture,
        CancellationToken ct)
    {
        await WriteEvidenceAsync(execution, capture.GitStatus, EvidenceKind.GitStatus, "text/plain", ct);
        await WriteEvidenceAsync(execution, capture.GitCommitGraph, EvidenceKind.GitCommitGraph, "text/plain", ct);
        await WriteEvidenceAsync(execution, capture.GitWorktreeDiff, EvidenceKind.GitWorktreeDiff, "text/plain", ct);
        await WriteEvidenceAsync(execution, capture.FileInventory, EvidenceKind.FileInventory, "text/plain", ct);

        await RecordEventAsync(
            execution,
            "ChangesCaptured",
            $"{capture.ChangedPaths.Count} changed path(s), head {Short(capture.ObservedHeadSha)}",
            ct);
    }

    /// <summary>
    /// Stores one blob and the row that points at it.
    ///
    /// <paramref name="stepId"/> is what keeps two producers of the same kind apart. The
    /// implementer's transcript belongs to the Agent step and the reviewer's to the Review
    /// step; without that, an execution would hold two AgentStdout artifacts with nothing to
    /// say which of them anybody was reading.
    ///
    /// Returns the row so a caller that needs to refer to this evidence later - the review,
    /// which is anchored to the stored commit diff - can hold the artifact rather than go
    /// looking for it by kind and hope there is only one.
    /// </summary>
    private async Task<EvidenceArtifact?> WriteEvidenceAsync(
        Execution execution,
        string text,
        EvidenceKind kind,
        string mediaType,
        CancellationToken ct,
        Guid? stepId = null)
    {
        var stored = await _evidence.WriteAsync(text, kind, mediaType, ct);
        if (stored is null)
        {
            return null;
        }

        var artifact = new EvidenceArtifact(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            execution.Id,
            stepId,
            kind,
            stored.Sha256,
            stored.SizeBytes,
            stored.MediaType,
            stored.RedactionCount,
            _clock.GetUtcNow());

        _store.AddEvidenceArtifact(artifact);

        await _store.SaveChangesAsync(ct);

        return artifact;
    }

    /// <summary>
    /// Records that the agent moved HEAD. Whether that decides the outcome depends on
    /// whether the agent step already failed: a crash that also committed is a failed
    /// execution with an unexpected commit recorded, not a policy violation.
    /// </summary>
    private async Task RecordUnexpectedCommitsAsync(
        Execution execution,
        ExecutionCapture capture,
        CancellationToken ct)
    {
        if (string.Equals(capture.BaseCommitSha, capture.ObservedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await PersistAsync(ct, () =>
        {
            execution.RecordUnexpectedCommits(capture.UnexpectedCommitShas, _clock.GetUtcNow());
            return AuditFor(
                execution,
                "UnexpectedAgentCommit",
                $"head {Short(capture.ObservedHeadSha)} is not the pinned base {Short(capture.BaseCommitSha)}");
        });
    }

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(12, sha.Length)];
}
