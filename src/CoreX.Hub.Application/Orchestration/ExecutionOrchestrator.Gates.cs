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
    ///
    /// Two tokens on purpose. <paramref name="ct"/> is the caller's and still governs the
    /// external work below - the result commit and the verification run - because those can
    /// legitimately be abandoned. <paramref name="record"/> is the bounded mandatory token
    /// and governs every write that closes the execution, because a cancelled run that
    /// could not write down how it ended is a row nobody can interpret afterwards.
    /// </summary>
    private async Task<ExecutionRunReport> EvaluateAndFinishAsync(
        Execution execution,
        HubTask task,
        PreparedExecutionWorkspace workspace,
        ExecutionCapture capture,
        Guid repositoryId,
        CancellationToken ct,
        CancellationToken record)
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
            await RecordEventAsync(execution, "Gate" + verdict.Kind, verdict.Describe(), record);
        }

        if (gates.BlockingVerdict is { } blocked)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Verify, "blocked by " + blocked.Kind, record);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

            // No result commit, no verification, and the worktree stays exactly as the
            // agent left it: whatever it wrote is the evidence for the block.
            return await CompleteAsync(execution, task, OutcomeFor(blocked.Kind), blocked.Reason, null, record);
        }

        // Cancellation checkpoint ----------------------------------------------------
        // Placed here, and not earlier, because the evidence above is already stored: the
        // run is cancelled with its account of itself intact. A commit created after the
        // caller has already said stop would be a permanent artefact of a run that was told
        // not to produce one, so cancellation wins before the commit exists and never
        // after.
        if (ct.IsCancellationRequested)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Verify, CancelledBeforeCommit, record);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

            return await TerminateAsync(
                execution,
                task,
                ExecutionState.Cancelled,
                "the run was cancelled before a result commit was created",
                record);
        }

        // Result commit --------------------------------------------------------------
        // The one authorised call. Everything about how a commit is made - identity,
        // hooks, no bypass flags - belongs to the workspace module.
        var commit = await _workspace.CreateResultCommitAsync(
            workspace, task.TaskKey.Value, execution.AttemptNo, ct);

        if (!commit.Created || commit.CommitSha is null)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Verify, "no result commit", record);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

            return await TerminateAsync(execution, task, ExecutionState.Failed,
                "the result commit was not created: " + commit.Detail, record);
        }

        await PersistAsync(record, () =>
        {
            execution.RecordResultCommit(commit.CommitSha, _clock.GetUtcNow());
            return AuditFor(execution, "ResultCommitCreated", "result commit " + commit.CommitSha[..12]);
        });

        if (commit.CommitDiff is { Length: > 0 })
        {
            await WriteEvidenceAsync(execution, commit.CommitDiff, EvidenceKind.GitCommitDiff, "text/plain", record);
        }

        // Verification ---------------------------------------------------------------
        // Never before a result commit exists: verifying an uncommitted tree would measure
        // something no one can go back and look at.
        await AdvanceAsync(execution, ExecutionState.Verifying, ExecutionStepKind.Verify, record);

        var verification = await _verifier.VerifyAsync(
            repositoryId, workspace, execution.BaseCommitSha, ct);

        await RecordEventAsync(execution, "Verification", verification.Detail, record);

        if (verification.Baseline is { } baseline)
        {
            await RecordEventAsync(
                execution,
                "Baseline",
                $"baseline at {baseline.BaseCommitSha[..12]}: {baseline.Description}",
                record);
        }

        if (verification.SnapshotResidual is { } residual)
        {
            // A snapshot that would not go away is worth saying out loud; it never changes
            // what the verification measured.
            await RecordEventAsync(execution, "SnapshotResidual", residual, record);
        }

        var passed = verification.Result?.RequiredPassed == true;

        // A verification the caller cancelled is a required step that did not succeed,
        // which the existing outcome rule already answers: the execution completes as
        // VerificationFailed. It does not become a terminal Cancelled, because the result
        // commit exists and a cancelled execution is defined as one that produced none -
        // and the commit is never rewritten or disowned to make a tidier story. The step
        // says Cancelled so the timeline still distinguishes "was stopped" from "failed".
        var cancelled = !passed && ct.IsCancellationRequested;

        await FinishStepAsync(
            execution.Id,
            ExecutionStepKind.Verify,
            passed ? StepFate.Succeeded : cancelled ? StepFate.Cancelled : StepFate.Failed,
            verification.Detail,
            record);

        // Review ---------------------------------------------------------------------
        // Phase 5 has no reviewer. The step is recorded as skipped with a reason rather
        // than as a clean review, because "nobody looked" is not "somebody approved".
        await AdvanceAsync(execution, ExecutionState.Reviewing, null, record);
        await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

        return await CompleteAsync(
            execution,
            task,
            passed ? ExecutionOutcome.Passed : ExecutionOutcome.VerificationFailed,
            verification.Detail,
            verification.SnapshotResidual,
            record);
    }

    private const string ReviewNotConfigured = "ReviewNotConfiguredInPhase5";

    private const string CancelledBeforeCommit = "CancelledBeforeResultCommit";

    private static ExecutionOutcome OutcomeFor(GateKind kind) => kind switch
    {
        GateKind.RuntimePolicy => ExecutionOutcome.RuntimePolicyViolation,
        GateKind.Scope => ExecutionOutcome.ScopeViolation,
        GateKind.Secret => ExecutionOutcome.SecretGuardBlocked,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown gate"),
    };
}
