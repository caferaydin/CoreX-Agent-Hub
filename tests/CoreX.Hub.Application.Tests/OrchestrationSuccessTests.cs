using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The Phase 5 Loop Zero happy path, end to end through a real repository, a real
/// workspace, a real agent process, real evidence and real verification.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationSuccessTests
{
    [Fact]
    public async Task Apply_patch_passes_and_hands_the_task_to_a_human()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);
        Assert.NotNull(report.ContextPackHash);
        Assert.False(report.UnexpectedAgentCommitDetected);

        // The task stops at the human, and no acceptance was written.
        var task = await hub.TaskAsync();
        Assert.Equal(TaskState.AwaitingAcceptance, task.State);
        Assert.Equal(0, await hub.Store.CountAcceptanceDecisionsAsync(report.ExecutionId, CancellationToken.None));

        // Every pipeline step ran, and review is skipped rather than approved.
        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.PrepareWorkspace));
        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.BuildContext));
        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.Agent));
        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.CaptureChanges));
        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.Verify));

        var review = steps.Single(s => s.Kind == ExecutionStepKind.Review);
        Assert.Equal(ExecutionStepState.Skipped, review.State);
        Assert.Equal("ReviewNotConfiguredInPhase5", review.StateReason);
    }

    [Fact]
    public async Task The_result_commit_belongs_to_the_hub()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.NotNull(report.ResultCommitSha);

        var author = hub.RunGit("log", "-1", "--format=%an <%ae>", report.ResultCommitSha!);
        Assert.Equal("CoreX Agent Hub <agent@corex.local>", author);

        // The commit is on the agent branch, and main never moved.
        var mainHead = hub.RunGit("rev-parse", "main");
        Assert.NotEqual(report.ResultCommitSha, mainHead);
    }

    [Fact]
    public async Task The_timeline_shows_every_stage()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        var types = events.Select(e => e.EventType).ToList();

        foreach (var expected in new[]
                 {
                     "PreparingWorkspace", "BuildingContext", "RunningAgent", "CapturingChanges",
                     "AgentFinished", "ChangesCaptured", "GateRuntimePolicy", "GateScope", "GateSecret",
                     "Verifying", "Verification", "Completed",
                 })
        {
            Assert.Contains(expected, types);
        }

        // Timelines carry summaries, never payloads.
        Assert.All(events, e => Assert.True(e.Message.Length <= 1000));
    }

    [Fact]
    public async Task Evidence_is_recorded_for_the_agent_and_for_the_working_tree()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var evidence = await hub.Store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);
        var kinds = evidence.Select(e => e.Kind).ToList();

        Assert.Contains(EvidenceKind.AgentStdout, kinds);
        Assert.Contains(EvidenceKind.GitStatus, kinds);
        Assert.Contains(EvidenceKind.GitWorktreeDiff, kinds);
        Assert.Contains(EvidenceKind.FileInventory, kinds);
        Assert.Contains(EvidenceKind.GitCommitDiff, kinds);

        // Metadata rows point at blobs; the payload never enters the database.
        Assert.All(evidence, e =>
        {
            Assert.Equal(64, e.Sha256.Length);
            Assert.True(hub.EvidenceStore.Exists(e.Sha256));
        });
    }

    private static ExecutionStepState State(IReadOnlyList<ExecutionStep> steps, ExecutionStepKind kind) =>
        steps.Single(s => s.Kind == kind).State;
}
