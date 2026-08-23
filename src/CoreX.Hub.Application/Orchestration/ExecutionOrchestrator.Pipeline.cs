using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    private async Task<ExecutionRunReport> RunOnceAsync(Guid taskId, string agentProfileName, CancellationToken ct)
    {
        var start = await StartAsync(taskId, agentProfileName, ct);
        var execution = start.Execution;
        var task = start.Task;

        using var heartbeat = new LeaseHeartbeat(this, execution.Id, _options.HeartbeatInterval, _clock);

        // Workspace ------------------------------------------------------------------
        await AdvanceAsync(execution, ExecutionState.PreparingWorkspace, ExecutionStepKind.PrepareWorkspace, ct);

        var workspace = await _workspace.PrepareAsync(
            start.RepositoryId, task.TaskKey.Value, execution.AttemptNo, ct);

        if (workspace is null)
        {
            await FinishStepAsync(execution.Id, ExecutionStepKind.PrepareWorkspace, StepFate.Failed,
                "the workspace could not be prepared", ct);

            return await TerminateAsync(execution, task, ExecutionState.Failed, "workspace preparation failed", ct);
        }

        // The pinned commit becomes this execution's provenance now, before anything else
        // can be measured against it.
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
            execution.Id, start.RepositoryId, task, execution.AttemptNo, workspace.BaseCommitSha, ct);

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

        // From here on the caller's token is deliberately not used. It has already done its
        // job - it stopped the agent - and reusing it would make a cancelled run destroy
        // the record of what that run did. Evidence is not optional work.
        var evidenceToken = CancellationToken.None;

        await WriteAgentEvidenceAsync(execution, agentResult, evidenceToken);
        await FinishStepAsync(execution.Id, ExecutionStepKind.Agent, StepFateFor(agentResult.Outcome),
            agentResult.Diagnostic, evidenceToken);

        // Capture --------------------------------------------------------------------
        // Deliberately reached whatever the agent did. A crashed or cancelled run still
        // leaves work on disk, and refusing to look at it would destroy the only record
        // of what happened. Cancellation is honoured for the agent, not for the evidence.
        await AdvanceAsync(execution, ExecutionState.CapturingChanges, ExecutionStepKind.CaptureChanges, evidenceToken);

        var capture = await _workspace.CaptureAsync(workspace, evidenceToken);
        await WriteCaptureEvidenceAsync(execution, capture, evidenceToken);
        await RecordUnexpectedCommitsAsync(execution, capture, evidenceToken);
        await FinishStepAsync(execution.Id, ExecutionStepKind.CaptureChanges, StepFate.Succeeded, null, evidenceToken);

        // K0 ------------------------------------------------------------------------
        if (TerminalStateFor(agentResult.Outcome) is { } terminal)
        {
            // Collision M4: the agent's own terminal failure owns the outcome even when it
            // also committed. The unexpected commit is recorded as diagnosis, not promoted
            // to a policy violation, and nothing repairs it.
            return await TerminateAsync(
                execution, task, terminal, agentResult.Diagnostic ?? agentResult.Outcome, evidenceToken);
        }

        return await EvaluateAndFinishAsync(execution, task, workspace, capture, start.RepositoryId, ct);
    }
}
