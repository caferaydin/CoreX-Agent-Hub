using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Application.Tests;

/// <summary>The gates, reached through the orchestrator rather than called directly.</summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationGateTests
{
    [Fact]
    public async Task An_unexpected_commit_blocks_on_the_runtime_policy_gate()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.UnexpectedCommit;

        Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", hub.Git);

        try
        {
            var report = await hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

            Assert.Equal(ExecutionState.Completed, report.State);
            Assert.Equal(ExecutionOutcome.RuntimePolicyViolation, report.Outcome);
            Assert.True(report.UnexpectedAgentCommitDetected);
            Assert.NotEmpty(report.UnexpectedCommitShas);
            Assert.Null(report.ResultCommitSha);

            await AssertBlockedAsync(hub, report.ExecutionId);

            // The agent's commit is still reachable: nothing repaired history.
            Assert.Equal(
                report.UnexpectedCommitShas[0],
                hub.RunGit("rev-parse", report.UnexpectedCommitShas[0]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", null);
        }
    }

    [Fact]
    public async Task An_out_of_scope_write_blocks_on_the_scope_gate()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.OutOfScopeWrite;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.ScopeViolation, report.Outcome);
        Assert.Null(report.ResultCommitSha);

        await AssertBlockedAsync(hub, report.ExecutionId);

        var diff = await OrchestrationFailureTests.ReadEvidenceAsync(
            hub, report.ExecutionId, EvidenceKind.GitWorktreeDiff);

        Assert.Contains("infra/deploy.yaml", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_write_blocks_without_persisting_the_secret()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.SecretWrite;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.SecretGuardBlocked, report.Outcome);
        Assert.Null(report.ResultCommitSha);

        await AssertBlockedAsync(hub, report.ExecutionId);

        // The gate saw the raw added line in memory; the store never did.
        foreach (var blob in hub.BlobFiles())
        {
            var stored = await File.ReadAllTextAsync(blob);

            foreach (var secret in SyntheticSecrets.AllSecretValues())
            {
                Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
            }
        }

        var diff = await OrchestrationFailureTests.ReadEvidenceAsync(
            hub, report.ExecutionId, EvidenceKind.GitWorktreeDiff);

        Assert.Contains("[REDACTED:OPENAI_TOKEN]", diff, StringComparison.Ordinal);

        // The timeline names the family, never the value.
        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        var secretEvent = events.Single(e => e.EventType == "GateSecret");

        Assert.Contains("OPENAI_TOKEN", secretEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticSecrets.OpenAiToken, secretEvent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_crash_that_also_committed_is_a_failure_not_a_policy_violation()
    {
        // Collision M4: the agent's own terminal failure owns the outcome. The commit is
        // recorded as diagnosis and left exactly where it is.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.UnexpectedCommitThenCrash;

        Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", hub.Git);

        try
        {
            var report = await hub.Orchestrator().RunAsync(
                hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

            Assert.Equal(ExecutionState.Failed, report.State);
            Assert.Null(report.Outcome);

            Assert.True(report.UnexpectedAgentCommitDetected);
            Assert.NotEmpty(report.UnexpectedCommitShas);
            Assert.Null(report.ResultCommitSha);

            var evidence = await hub.Store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);
            Assert.Contains(evidence, e => e.Kind == EvidenceKind.GitCommitGraph);

            var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
            Assert.Equal(ExecutionStepState.Pending, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
            Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
            Assert.True((await hub.ExecutionAsync(report.ExecutionId)).WorktreeMustBePreserved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", null);
        }
    }

    private static async Task AssertBlockedAsync(OrchestrationHarness hub, Guid executionId)
    {
        var steps = await hub.Store.ListExecutionStepsAsync(executionId, CancellationToken.None);

        Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);

        var review = steps.Single(s => s.Kind == ExecutionStepKind.Review);
        Assert.Equal(ExecutionStepState.Skipped, review.State);
        Assert.Equal("ReviewNotConfigured", review.StateReason);

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);

        var execution = await hub.ExecutionAsync(executionId);
        Assert.True(execution.WorktreeMustBePreserved);
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);
    }
}
