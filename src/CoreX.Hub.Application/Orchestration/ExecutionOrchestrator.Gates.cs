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

        // Held rather than merely written: the review is anchored to this exact artifact,
        // and looking it up later by kind would mean guessing which of several rows was meant.
        var commitDiff = commit.CommitDiff is { Length: > 0 }
            ? await WriteEvidenceAsync(
                execution, commit.CommitDiff, EvidenceKind.GitCommitDiff, "text/plain", record)
            : null;

        // The commit staged everything, so the worktree should now be exactly the commit.
        // If it is not, the tree and the commit are different things and nothing may be
        // measured or released on the assumption that they are the same - so this fails
        // closed rather than continuing. Nothing is deleted to make it true.
        if (!commit.WorktreeStateKnown || commit.UncommittedPaths.Count > 0)
        {
            // A tree nobody could inspect is not a clean tree. Both answers stop here,
            // because both mean the same thing: it cannot be shown that the tree is the
            // commit.
            var why = commit.WorktreeStateKnown
                ? $"the worktree still holds {commit.UncommittedPaths.Count} uncommitted path(s) after the "
                  + $"result commit ({string.Join(", ", commit.UncommittedPaths.Take(5))})"
                : "the worktree state could not be read after the result commit";

            await SkipAsync(execution.Id, ExecutionStepKind.Verify, WorktreeNotAtResultCommit, record);
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

            return await CompleteAsync(
                execution, task, ExecutionOutcome.VerificationFailed, why + ", so nothing was verified",
                null, record);
        }

        // Verification ---------------------------------------------------------------
        // Never before a result commit exists: verifying an uncommitted tree would measure
        // something no one can go back and look at.
        //
        // Both measurements happen in a disposable tree pinned to a commit - the result at
        // this execution's own commit, the baseline at the one it started from. The agent's
        // worktree is deliberately not one of them: build output belongs in something
        // throwaway, and a worktree full of it could not be released afterwards without
        // forcing, which the Hub does not do.
        await AdvanceAsync(execution, ExecutionState.Verifying, ExecutionStepKind.Verify, record);

        var verification = await _verifier.VerifyAsync(
            repositoryId, execution.BaseCommitSha, commit.CommitSha, ct);

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

        // The step and its commands are written together: what each verification step was
        // called, whether it was required at the time and how it ended is the only thing a
        // Machine acceptance criterion can be answered from once this process is gone.
        await FinishVerifyStepAsync(
            execution,
            passed ? StepFate.Succeeded : cancelled ? StepFate.Cancelled : StepFate.Failed,
            verification.Detail,
            verification.Result?.Steps ?? [],
            record);

        // Review ---------------------------------------------------------------------
        // Only a result that passed the verification required of it is reviewed. Reviewing
        // one that failed would spend a reviewer on a state the machine has already refused,
        // and would invite the review to become an argument against a mechanical answer.
        await AdvanceAsync(
            execution,
            ExecutionState.Reviewing,
            passed && _reviewer is not null ? ExecutionStepKind.Review : null,
            record);

        if (!passed)
        {
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewSkippedVerificationFailed, record);

            return await CompleteAsync(
                execution,
                task,
                ExecutionOutcome.VerificationFailed,
                verification.Detail,
                verification.SnapshotResidual,
                record);
        }

        if (_reviewer is null)
        {
            // No reviewer is configured for this repository. The step says so with a reason
            // rather than reporting a clean review, because "nobody looked" is not "somebody
            // approved" - and the execution still passes, since the verification that was
            // required of it did.
            await SkipAsync(execution.Id, ExecutionStepKind.Review, ReviewNotConfigured, record);

            return await CompleteAsync(
                execution,
                task,
                ExecutionOutcome.Passed,
                verification.Detail,
                verification.SnapshotResidual,
                record);
        }

        return await ReviewAndFinishAsync(
            execution,
            task,
            repositoryId,
            commit.CommitSha,
            changes.ChangedPaths,
            commitDiff,
            verification.SnapshotResidual,
            ct,
            record);
    }

    private const string ReviewNotConfigured = "ReviewNotConfigured";

    private const string CancelledBeforeCommit = "CancelledBeforeResultCommit";

    private const string WorktreeNotAtResultCommit = "WorktreeNotAtResultCommit";

    private static ExecutionOutcome OutcomeFor(GateKind kind) => kind switch
    {
        GateKind.RuntimePolicy => ExecutionOutcome.RuntimePolicyViolation,
        GateKind.Scope => ExecutionOutcome.ScopeViolation,
        GateKind.Secret => ExecutionOutcome.SecretGuardBlocked,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown gate"),
    };
}
