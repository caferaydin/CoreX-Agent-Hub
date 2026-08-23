using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Cli.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Workspace;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Releasing a real worktree after a real decision, through the real workspace module.
///
/// The fake-port tests say what acceptance asks for; these say what happens on disk when it
/// asks. Two things matter and are checked directly: the directory goes, and the branch and
/// the commit stay - releasing a worktree is housekeeping, never a way to undo work.
///
/// One limit is proven here rather than hidden: the workspace module does not force a
/// removal, so a worktree still holding uncommitted files is reported as orphaned instead.
/// After a full pipeline run that is the normal case, because verification writes its report
/// into the execution worktree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceReleaseGatewayTests
{
    private const string HumanCriterion = "The wording reads well.";

    private static Task<OrchestrationHarness> HubAsync() =>
        OrchestrationHarness.CreateAsync(humanCriteria: [HumanCriterion]);

    private static WorkspaceReleaseGateway GatewayFor(OrchestrationHarness hub) =>
        new(
            hub.WorkspaceManager,
            _ => new RepositoryLocation(
                hub.RepositoryPath, hub.Root, hub.WorkspaceRoot, "hub-dev", "hub-sample", "main"));

    private static AcceptanceService ServiceFor(OrchestrationHarness hub) =>
        new(hub.Store, new VerificationTruthReader(hub.Store), GatewayFor(hub));

    private static async Task<AcceptanceRequest> AcceptRequestAsync(OrchestrationHarness hub) => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = 1,
        Actor = "cafer",
        HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
    };

    [Fact]
    public async Task Accepting_removes_the_worktree_and_keeps_the_branch_and_the_commit()
    {
        await using var hub = await HubAsync();

        var prepared = await PrepareCommittedWorkspaceAsync(hub);
        var executionId = await PersistPassedAsync(hub, prepared.Workspace, prepared.CommitSha);

        Assert.True(Directory.Exists(prepared.Workspace.WorkspacePath));

        var result = await ServiceFor(hub).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Released, result.WorkspaceCleanup.Outcome);

        // The directory is gone, and git no longer lists it.
        Assert.False(Directory.Exists(prepared.Workspace.WorkspacePath));
        Assert.DoesNotContain(
            prepared.Workspace.WorkspacePath,
            hub.RunGit("worktree", "list"),
            StringComparison.OrdinalIgnoreCase);

        // The work itself is untouched: the branch still points at the Hub's commit, and the
        // commit still resolves. Nothing was merged, pushed or deleted.
        Assert.Equal(prepared.CommitSha, hub.RunGit("rev-parse", prepared.Workspace.BranchName));
        Assert.Equal(prepared.CommitSha, hub.RunGit("rev-parse", prepared.CommitSha));

        var after = await hub.ExecutionAsync(executionId);

        Assert.Equal(WorkspaceState.Released, after.WorkspaceState);
        Assert.Equal(prepared.CommitSha, after.ResultCommitSha);
        Assert.Equal(ExecutionOutcome.Passed, after.Outcome);
    }

    [Fact]
    public async Task Rejecting_removes_it_too_and_still_keeps_the_branch()
    {
        await using var hub = await HubAsync();

        var prepared = await PrepareCommittedWorkspaceAsync(hub);
        var executionId = await PersistPassedAsync(hub, prepared.Workspace, prepared.CommitSha);

        var result = await ServiceFor(hub).RejectAsync(
            new TaskDecisionRequest
            {
                TaskKey = "HUB-001",
                AttemptNo = 1,
                Actor = "cafer",
                Reason = "we are not doing this after all",
            },
            hub.Clock.GetUtcNow(),
            CancellationToken.None);

        Assert.Equal(TaskState.Rejected, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Released, result.WorkspaceCleanup.Outcome);

        Assert.False(Directory.Exists(prepared.Workspace.WorkspacePath));
        Assert.Equal(prepared.CommitSha, hub.RunGit("rev-parse", prepared.Workspace.BranchName));
        Assert.Equal(WorkspaceState.Released, (await hub.ExecutionAsync(executionId)).WorkspaceState);
    }

    [Fact]
    public async Task A_worktree_still_holding_uncommitted_files_is_orphaned_and_the_decision_stands()
    {
        // The real pipeline leaves verification output in the execution worktree, and the
        // workspace module never forces a removal. So this is what an accepted run normally
        // reports today - said out loud rather than smoothed over, because the tree really
        // is still on disk and somebody has to know.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var before = await hub.ExecutionAsync(executionId);
        Assert.True(Directory.Exists(before.WorktreePath));

        var result = await ServiceFor(hub).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        // The decision succeeded; only the cleanup did not.
        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Orphaned, result.WorkspaceCleanup.Outcome);

        var after = await hub.ExecutionAsync(executionId);

        // Still Active, because the tree really is still there. Nothing was deleted behind
        // git's refusal.
        Assert.Equal(WorkspaceState.Active, after.WorkspaceState);
        Assert.True(Directory.Exists(after.WorktreePath));
        Assert.Equal(ExecutionOutcome.Passed, after.Outcome);
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task The_workspace_refuses_to_release_a_worktree_holding_the_only_copy()
    {
        // D6 at the authority itself, not at the caller. Even asked directly - which the
        // service never does - the workspace module denies, because an execution with no
        // commit has its work nowhere else.
        await using var hub = await HubAsync();
        hub.Mode = Agents.Scripted.ScriptedAgentMode.Crash;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Null(report.ResultCommitSha);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        Assert.True(execution.WorktreeMustBePreserved);
        Assert.True(Directory.Exists(execution.WorktreePath));

        var cleanup = await GatewayFor(hub).ReleaseAsync(
            hub.RepositoryId, execution, CancellationToken.None);

        Assert.Equal(WorkspaceCleanupOutcome.DeniedNoResultCommit, cleanup.Outcome);

        // The only copy of the work is still there.
        Assert.True(Directory.Exists(execution.WorktreePath));
        Assert.Equal(WorkspaceState.Active, (await hub.ExecutionAsync(report.ExecutionId)).WorkspaceState);
    }

    /// <summary>A real agent worktree with a real Hub commit and nothing left uncommitted.</summary>
    private static async Task<(PreparedWorkspace Workspace, string CommitSha)> PrepareCommittedWorkspaceAsync(
        OrchestrationHarness hub)
    {
        var prepared = await hub.WorkspaceManager.PrepareAsync(
            new WorkspaceRequest
            {
                RepositoryPath = hub.RepositoryPath,
                RepositoryTrustRoot = hub.Root,
                WorkspaceRoot = hub.WorkspaceRoot,
                OrganizationSlug = "hub-dev",
                RepositorySlug = "hub-sample",
                TaskKey = "HUB-001",
                AttemptNo = 1,
                DefaultBranch = "main",
            },
            CancellationToken.None);

        Assert.True(prepared.Succeeded, prepared.Describe());

        var workspace = prepared.Workspace!;

        File.WriteAllText(Path.Combine(workspace.WorkspacePath, "src", "added.cs"), "// added\n");

        var commit = await hub.WorkspaceManager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.Created, commit.State);

        return (workspace, commit.CommitSha!);
    }

    /// <summary>An execution row describing that worktree, awaiting a person.</summary>
    private static async Task<Guid> PersistPassedAsync(
        OrchestrationHarness hub,
        PreparedWorkspace workspace,
        string commitSha)
    {
        var now = hub.Clock.GetUtcNow();
        var task = await hub.TaskAsync();

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            hub.OrganizationId,
            hub.TaskId,
            1,
            workspace.BaseCommitSha,
            workspace.BranchName,
            workspace.WorkspacePath,
            null,
            now);

        var verify = new ExecutionStep(
            Guid.CreateVersion7(), hub.OrganizationId, execution.Id, 5, ExecutionStepKind.Verify, now);

        verify.Succeed(now);

        execution.RecordResultCommit(commitSha, now);
        execution.Complete(ExecutionOutcome.Passed, now);

        task.MarkRunning(now);
        task.MarkAwaitingAcceptance(now);

        hub.Store.AddExecution(execution);
        hub.Store.AddExecutionStep(verify);

        hub.Store.AddCommandResult(new CommandResult(
            Guid.CreateVersion7(), hub.OrganizationId, verify.Id, 1, "test", "fixture",
            ["report", "results.trx"], VerificationCommandStatus.Passed, required: true,
            exitCode: 0, durationMilliseconds: 900, testsPassed: 11, testsFailed: 0, testsSkipped: 0, now));

        await hub.Store.SaveChangesAsync(CancellationToken.None);

        return execution.Id;
    }
}
