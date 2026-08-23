using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// What reconciliation does with an execution nobody is driving any more, on both sides of
/// the result commit.
///
/// Before the commit there is nothing to lose: the attempt produced nothing, Interrupted
/// says exactly that, and the task goes back to the queue. After the commit there is
/// something the Hub does not own - an object in the repository - and no terminal error
/// state can describe an attempt that produced one. The only ways to make Interrupted true
/// would be to drop the commit from the record or to remove it from the repository, and
/// both are lies about a repository the Hub is a guest in. So that case is reported and
/// left alone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationRecoveryTests
{
    [Fact]
    public async Task A_stale_execution_without_a_commit_is_interrupted_as_before()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.Crash;

        // A crashed agent leaves a real execution with real evidence and no commit, which
        // is the ordinary shape of an abandoned attempt.
        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Null(report.ResultCommitSha);

        // A second attempt, abandoned mid pipeline this time.
        var abandoned = await AbandonBeforeCommitAsync(hub);

        hub.Liveness.OwnerIsAlive = false;

        var reconciled = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        var interrupted = Assert.Single(reconciled.Interrupted);
        Assert.Equal(abandoned.Id, interrupted.ExecutionId);
        Assert.Empty(reconciled.RecoveryRequired);

        var afterwards = await hub.ExecutionAsync(abandoned.Id);

        Assert.Equal(ExecutionState.Interrupted, afterwards.State);
        Assert.Null(afterwards.ResultCommitSha);
        Assert.True(afterwards.WorktreeMustBePreserved);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task A_stale_execution_that_already_committed_is_reported_rather_than_interrupted()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // The host dies after the Hub result commit exists and before the pipeline could
        // finish with it: the crash window the architecture keeps explicit.
        hub.DecorateVerifier = inner => new ScriptedVerifier(inner)
        {
            BeforeVerify = () => throw new InvalidOperationException("the host died after the result commit"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        var stranded = Assert.Single(
            await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        Assert.False(stranded.IsTerminal);
        Assert.NotNull(stranded.ResultCommitSha);

        var commit = stranded.ResultCommitSha!;
        var branchHeadBefore = hub.RunGit("rev-parse", stranded.BranchName);

        hub.Liveness.OwnerIsAlive = false;

        var reconciled = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        // Reported, not closed.
        Assert.Empty(reconciled.Interrupted);

        var recovery = Assert.Single(reconciled.RecoveryRequired);
        Assert.Equal(stranded.Id, recovery.ExecutionId);
        Assert.Equal(commit, recovery.ResultCommitSha);
        Assert.Equal(ExecutionState.Verifying, recovery.State);
        Assert.Contains("gone", recovery.Reason, StringComparison.Ordinal);
        Assert.Contains("recovery", reconciled.Describe(), StringComparison.OrdinalIgnoreCase);

        var afterwards = await hub.ExecutionAsync(stranded.Id);

        // The execution is exactly where it was: no false terminal state was written.
        Assert.False(afterwards.IsTerminal);
        Assert.Equal(ExecutionState.Verifying, afterwards.State);
        Assert.Equal(commit, afterwards.ResultCommitSha);

        // The commit is untouched, and so is the branch it sits on: nothing was reset,
        // rewritten, amended or force updated to make the row easier to close.
        Assert.Equal(commit, hub.RunGit("rev-parse", commit));
        Assert.Equal(branchHeadBefore, hub.RunGit("rev-parse", afterwards.BranchName));
        Assert.Equal(commit, branchHeadBefore);

        // The worktree stays, and the task is neither handed back nor accepted.
        Assert.True(Directory.Exists(afterwards.WorktreePath));

        var task = await hub.TaskAsync();
        Assert.Equal(TaskState.Running, task.State);
        Assert.NotEqual(TaskState.Accepted, task.State);
    }

    [Fact]
    public async Task Reporting_a_stranded_execution_changes_nothing_however_often_it_runs()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        hub.DecorateVerifier = inner => new ScriptedVerifier(inner)
        {
            BeforeVerify = () => throw new InvalidOperationException("the host died after the result commit"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None));

        var stranded = Assert.Single(
            await hub.Store.ListExecutionsForTaskAsync(hub.TaskId, CancellationToken.None));

        hub.Liveness.OwnerIsAlive = false;

        var updatedBefore = stranded.UpdatedAtUtc;

        var first = await hub.Reconciler().ReconcileAsync(CancellationToken.None);
        var second = await hub.Reconciler().ReconcileAsync(CancellationToken.None);

        // The report is a diagnostic, not a transition: nothing is written, so saying it
        // twice says the same thing twice rather than compounding.
        Assert.Single(first.RecoveryRequired);
        Assert.Single(second.RecoveryRequired);

        var afterwards = await hub.ExecutionAsync(stranded.Id);

        Assert.Equal(updatedBefore, afterwards.UpdatedAtUtc);
        Assert.Equal(ExecutionState.Verifying, afterwards.State);
        Assert.NotNull(afterwards.ResultCommitSha);
    }

    /// <summary>A claimed, non terminal execution with no commit, exactly as a crash leaves one.</summary>
    private static async Task<DomainExecution> AbandonBeforeCommitAsync(OrchestrationHarness hub)
    {
        var now = hub.Clock.GetUtcNow();
        var task = await hub.TaskAsync();

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            hub.OrganizationId,
            hub.TaskId,
            2,
            hub.RunGit("rev-parse", "HEAD"),
            "agent/HUB-001/2",
            Path.Combine(hub.WorkspaceRoot, "abandoned"),
            null,
            now);

        execution.Advance(ExecutionState.RunningAgent, now);
        execution.ClaimLease(hub.Liveness.CurrentProcessId, hub.Liveness.CurrentHostStartedAtUtc, now);
        task.MarkRunning(now);

        hub.Store.AddExecution(execution);
        await hub.Store.SaveChangesAsync(CancellationToken.None);

        return execution;
    }
}
