using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// What happens when the run does not go well. Every case here has to leave the work
/// visible: evidence captured, worktree preserved, nothing committed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationFailureTests
{
    [Theory]
    [InlineData(ScriptedAgentMode.Crash, ExecutionState.Failed)]
    [InlineData(ScriptedAgentMode.PartialWrite, ExecutionState.Failed)]
    public async Task A_failing_agent_still_leaves_evidence_behind(ScriptedAgentMode mode, ExecutionState expected)
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = mode;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(expected, report.State);
        Assert.Null(report.Outcome);
        Assert.Null(report.ResultCommitSha);

        // Capture happened even though the agent failed: refusing to look would destroy
        // the only record of what it managed to do.
        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Equal(
            ExecutionStepState.Succeeded,
            steps.Single(s => s.Kind == ExecutionStepKind.CaptureChanges).State);

        // Git status is always captured. A crash that wrote nothing produces an empty diff,
        // and an empty blob is not evidence of anything, so only the modes that actually
        // wrote something are asserted to have one.
        var evidence = await hub.Store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);
        Assert.Contains(evidence, e => e.Kind == EvidenceKind.GitStatus);
        Assert.Contains(evidence, e => e.Kind == EvidenceKind.AgentStdout);

        // Verification never ran and the task is available again.
        Assert.Equal(ExecutionStepState.Pending, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);

        // The worktree holds the only copy of the work, so it stays.
        var execution = await hub.ExecutionAsync(report.ExecutionId);
        Assert.True(execution.WorktreeMustBePreserved);
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);
    }

    [Fact]
    public async Task A_partial_write_stays_visible_in_the_captured_diff()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.PartialWrite;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionState.Failed, report.State);

        // Half the work landed and nothing rolled it back.
        var diff = await ReadEvidenceAsync(hub, report.ExecutionId, EvidenceKind.GitWorktreeDiff);
        Assert.Contains("step-one.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hanging_agent_times_out_and_the_process_tree_is_gone()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.Hang;

        var before = System.Diagnostics.Process.GetProcessesByName("corex-fixture").Length;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionState.TimedOut, report.State);
        Assert.Null(report.ResultCommitSha);

        var after = System.Diagnostics.Process.GetProcessesByName("corex-fixture").Length;
        Assert.True(after <= before, $"a fixture process survived the timeout ({before} -> {after})");

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Equal(ExecutionStepState.TimedOut, steps.Single(s => s.Kind == ExecutionStepKind.Agent).State);
        Assert.Equal(ExecutionStepState.Succeeded, steps.Single(s => s.Kind == ExecutionStepKind.CaptureChanges).State);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task A_cancelled_run_still_captures_its_evidence()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.Hang;

        using var cancellation = new CancellationTokenSource();

        // Cancel exactly when the run reaches the agent, rather than after a fixed wait. A
        // wait is a guess about how fast this machine is: observed under load during the
        // Phase 6 acceptance, three seconds expired while the pipeline was still preparing a
        // worktree, so the run cancelled an early stage and this test asserted about
        // something else entirely.
        hub.DecorateAgent = inner => new AnnouncingAgentGateway(inner, () => cancellation.Cancel());

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.ResultCommitSha);

        // The caller's cancellation stopped the agent; it did not stop the evidence.
        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Equal(ExecutionStepState.Cancelled, steps.Single(s => s.Kind == ExecutionStepKind.Agent).State);
        Assert.Equal(ExecutionStepState.Succeeded, steps.Single(s => s.Kind == ExecutionStepKind.CaptureChanges).State);

        var evidence = await hub.Store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);
        Assert.Contains(evidence, e => e.Kind == EvidenceKind.GitStatus);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    internal static async Task<string> ReadEvidenceAsync(
        OrchestrationHarness hub,
        Guid executionId,
        EvidenceKind kind)
    {
        var evidence = await hub.Store.ListEvidenceAsync(executionId, CancellationToken.None);
        var artifact = evidence.First(e => e.Kind == kind);

        return await hub.EvidenceStore.ReadTextAsync(artifact.Sha256, CancellationToken.None);
    }
}
