using CoreX.Hub.Application.Gates;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    /// <summary>
    /// K1, K2, K3, then the result commit, then verification.
    ///
    /// Reached only when the agent step itself succeeded. Everything here works from the
    /// capture that already happened, so a gate never touches git or the disk.
    /// </summary>
    private async Task<ExecutionRunReport> EvaluateAndFinishAsync(
        Execution execution,
        HubTask task,
        PreparedExecutionWorkspace workspace,
        ExecutionCapture capture,
        Guid repositoryId,
        CancellationToken ct)
    {
        var changes = new CapturedChanges
        {
            BaseCommitSha = capture.BaseCommitSha,
            ObservedHeadSha = capture.ObservedHeadSha,
            UnexpectedCommitShas = capture.UnexpectedCommitShas,
            ChangedPaths = capture.ChangedPaths,
            AddedLines = capture.AddedLines,
        };

        var gates = _gates.Evaluate(changes, task.AllowedScope, task.ForbiddenScope);

        foreach (var verdict in gates.Verdicts)
        {
            await RecordEventAsync(execution, "Gate" + verdict.Kind, verdict.Describe(), ct);
        }

        if (gates.BlockingVerdict is { } blocked)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Verify, "blocked by " + blocked.Kind, ct);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, ct);

            // No result commit, no verification, and the worktree stays exactly as the
            // agent left it: whatever it wrote is the evidence for the block.
            return await CompleteAsync(execution, task, OutcomeFor(blocked.Kind), blocked.Reason, null, ct);
        }

        // Result commit --------------------------------------------------------------
        // The one authorised call. Everything about how a commit is made - identity,
        // hooks, no bypass flags - belongs to the workspace module.
        var commit = await _workspace.CreateResultCommitAsync(
            workspace, task.TaskKey.Value, execution.AttemptNo, ct);

        if (!commit.Created || commit.CommitSha is null)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Verify, "no result commit", ct);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, ct);

            return await TerminateAsync(execution, task, ExecutionState.Failed,
                "the result commit was not created: " + commit.Detail, ct);
        }

        await PersistAsync(ct, () =>
        {
            execution.RecordResultCommit(commit.CommitSha, _clock.GetUtcNow());
            return AuditFor(execution, "ResultCommitCreated", "result commit " + commit.CommitSha[..12]);
        });

        if (commit.CommitDiff is { Length: > 0 })
        {
            await WriteEvidenceAsync(execution, commit.CommitDiff, EvidenceKind.GitCommitDiff, "text/plain", ct);
        }

        // Verification ---------------------------------------------------------------
        // Never before a result commit exists: verifying an uncommitted tree would measure
        // something no one can go back and look at.
        await AdvanceAsync(execution, ExecutionState.Verifying, ExecutionStepKind.Verify, ct);

        var verification = await _verifier.VerifyAsync(
            repositoryId, workspace, execution.BaseCommitSha, ct);

        await RecordEventAsync(execution, "Verification", verification.Detail, ct);

        if (verification.Baseline is { } baseline)
        {
            await RecordEventAsync(
                execution,
                "Baseline",
                $"baseline at {baseline.BaseCommitSha[..12]}: {baseline.Description}",
                ct);
        }

        if (verification.SnapshotResidual is { } residual)
        {
            // A snapshot that would not go away is worth saying out loud; it never changes
            // what the verification measured.
            await RecordEventAsync(execution, "SnapshotResidual", residual, ct);
        }

        var passed = verification.Result?.RequiredPassed == true;

        await FinishStepAsync(execution.Id, ExecutionStepKind.Verify,
            passed ? StepFate.Succeeded : StepFate.Failed, verification.Detail, ct);

        // Review ---------------------------------------------------------------------
        // Phase 5 has no reviewer. The step is recorded as skipped with a reason rather
        // than as a clean review, because "nobody looked" is not "somebody approved".
        await AdvanceAsync(execution, ExecutionState.Reviewing, null, ct);
        await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, ct);

        return await CompleteAsync(
            execution,
            task,
            passed ? ExecutionOutcome.Passed : ExecutionOutcome.VerificationFailed,
            verification.Detail,
            verification.SnapshotResidual,
            ct);
    }

    private const string ReviewNotConfigured = "ReviewNotConfiguredInPhase5";

    private static ExecutionOutcome OutcomeFor(GateKind kind) => kind switch
    {
        GateKind.RuntimePolicy => ExecutionOutcome.RuntimePolicyViolation,
        GateKind.Scope => ExecutionOutcome.ScopeViolation,
        GateKind.Secret => ExecutionOutcome.SecretGuardBlocked,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown gate"),
    };
}
