using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    private async Task<ExecutionRunReport> RunOnceAsync(
        Guid taskId,
        string agentProfileName,
        Action<Guid>? onExecutionStarted,
        CancellationToken ct)
    {
        var validated = await ValidateStartAsync(taskId, agentProfileName, ct);

        // Provenance -----------------------------------------------------------------
        // The commit is resolved before the execution row exists. An execution that named a
        // commit nobody had resolved would still be measured against it - by the baseline
        // key, by the context pack, by every captured diff - so there is deliberately no
        // moment at which this row holds a placeholder waiting to be corrected.
        //
        // A repository that cannot answer therefore produces no execution at all: the task
        // never leaves Ready, and there is no half started attempt for anyone to reconcile.
        var preflight = await _workspace.PreflightAsync(validated.RepositoryId, ct);

        if (preflight.BaseCommitSha is not { } baseCommitSha)
        {
            throw new ExecutionStartException(
                $"No base commit could be pinned for task '{validated.Task.TaskKey}': {preflight.Detail}");
        }

        var start = await PersistStartAsync(validated, baseCommitSha, ct);
        var execution = start.Execution;
        var task = start.Task;

        using var heartbeat = new LeaseHeartbeat(this, execution.Id, _options.HeartbeatInterval, _clock);

        // The attempt now has an identity, and nothing external has happened yet. A host that
        // wants to be able to stop this particular run has to be told here: earlier there was
        // no id to name, and later the agent would already be running.
        onExecutionStarted?.Invoke(execution.Id);

        // Workspace ------------------------------------------------------------------
        await AdvanceAsync(execution, ExecutionState.PreparingWorkspace, ExecutionStepKind.PrepareWorkspace, ct);

        // The same pinned commit is handed back, so the worktree is materialised from what
        // this execution already claims to be based on. The branch is free to move in this
        // window; it simply stops being the authority the moment the commit was captured.
        var workspace = await _workspace.PrepareAsync(
            start.RepositoryId, preflight, task.TaskKey.Value, execution.AttemptNo, ct);

        if (workspace is null)
        {
            await FinishStepAsync(execution.Id, ExecutionStepKind.PrepareWorkspace, StepFate.Failed,
                "the workspace could not be prepared", ct);

            return await TerminateAsync(execution, task, ExecutionState.Failed, "workspace preparation failed", ct);
        }

        // Confirmation, not assignment: the aggregate refuses a workspace built from any
        // commit other than the one it was created with, so a drifted preparation fails
        // closed here rather than quietly becoming this execution's new provenance.
        await PersistAsync(ct, () =>
        {
            execution.RecordWorkspace(
                workspace.BaseCommitSha, workspace.BranchName, workspace.WorkspacePath, _clock.GetUtcNow());

            return AuditFor(
                execution, "WorkspacePrepared", $"{workspace.BranchName} at {workspace.BaseCommitSha[..12]}");
        });

        await FinishStepAsync(execution.Id, ExecutionStepKind.PrepareWorkspace, StepFate.Succeeded, null, ct);

        // Context --------------------------------------------------------------------
        await AdvanceAsync(execution, ExecutionState.BuildingContext, ExecutionStepKind.BuildContext, ct);

        var context = await _context.BuildAsync(
            execution.Id, start.RepositoryId, task, execution.AttemptNo, execution.BaseCommitSha, ct);

        if (context is null)
        {
            await FinishStepAsync(execution.Id, ExecutionStepKind.BuildContext, StepFate.Failed,
                "the context pack could not be built", ct);

            // No agent call and no result commit: the agent would have had nothing
            // trustworthy to work from.
            return await TerminateAsync(execution, task, ExecutionState.Failed, "context build failed", ct);
        }

        await PersistAsync(ct, () =>
        {
            execution.RecordContextPack(context.ContextPackHash, _clock.GetUtcNow());
            return AuditFor(execution, "ContextPackBuilt", "context pack " + context.ContextPackHash[..12]);
        });

        await FinishStepAsync(execution.Id, ExecutionStepKind.BuildContext, StepFate.Succeeded, null, ct);

        // Agent ----------------------------------------------------------------------
        await AdvanceAsync(execution, ExecutionState.RunningAgent, ExecutionStepKind.Agent, ct);

        var agentResult = await _agent.RunAsync(
            start.AgentProfile, workspace, context, task, execution.Id.ToString("D"), ct);

        // From here the run owes a record of itself whatever the caller decided. The
        // caller's token has already done its job - it stopped the agent - and reusing it
        // for the evidence would let a cancelled run destroy the only account of what it
        // did. So the mandatory sections below run on their own token.
        //
        // It is bounded rather than absolute: the sections that must finish get a budget,
        // not a blank cheque, and everything that is still real forward work below keeps
        // honouring the caller.
        //
        // The budget starts when the caller stops, not when this line runs. What follows is
        // interleaved with work that legitimately takes as long as it takes - a full
        // verification run over a real repository is minutes at least - and a countdown
        // started here would make this a deadline on that work too, killing every honest
        // long run at the first bookkeeping call after it.
        using var mandatory = MandatoryWorkScope.WhenCallerStops(_options.MandatoryWorkBudget, ct, _clock);
        var record = mandatory.Token;

        await WriteAgentEvidenceAsync(execution, agentResult, record);
        await FinishStepAsync(execution.Id, ExecutionStepKind.Agent, StepFateFor(agentResult.Outcome),
            agentResult.Diagnostic, record);

        // Capture --------------------------------------------------------------------
        // Deliberately reached whatever the agent did. A crashed or cancelled run still
        // leaves work on disk, and refusing to look at it would destroy the only record
        // of what happened. Cancellation is honoured for the agent, not for the evidence.
        await AdvanceAsync(execution, ExecutionState.CapturingChanges, ExecutionStepKind.CaptureChanges, record);

        var capture = await _workspace.CaptureAsync(workspace, record);
        await WriteCaptureEvidenceAsync(execution, capture, record);
        await RecordUnexpectedCommitsAsync(execution, capture, record);
        await FinishStepAsync(execution.Id, ExecutionStepKind.CaptureChanges, StepFate.Succeeded, null, record);

        // K0 ------------------------------------------------------------------------
        if (TerminalStateFor(agentResult.Outcome) is { } terminal)
        {
            // Collision M4: the agent's own terminal failure owns the outcome even when it
            // also committed. The unexpected commit is recorded as diagnosis, not promoted
            // to a policy violation, and nothing repairs it.
            return await TerminateAsync(
                execution, task, terminal, agentResult.Diagnostic ?? agentResult.Outcome, record);
        }

        return await EvaluateAndFinishAsync(execution, task, workspace, capture, start.RepositoryId, ct, record);
    }
}
