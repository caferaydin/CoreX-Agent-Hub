using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Composition.Orchestration;
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
    public async Task The_real_pipeline_leaves_a_worktree_clean_enough_to_release_without_forcing()
    {
        // The whole point of measuring the result in a throwaway tree. Verification fills a
        // tree with build output; when that happened in the execution worktree the tree was
        // dirty at decision time and git refused to remove it, so an accepted run reported
        // Orphaned. Now the output lands in a snapshot, the worktree is exactly the commit,
        // and the ordinary non-forcing removal succeeds.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var before = await hub.ExecutionAsync(executionId);
        var worktree = before.WorktreePath;
        var commit = before.ResultCommitSha!;

        Assert.True(Directory.Exists(worktree));

        // Nothing uncommitted is left behind by the run itself.
        Assert.Equal(string.Empty, hub.RunGitIn(worktree, "status", "--porcelain"));
        Assert.Equal(commit, hub.RunGitIn(worktree, "rev-parse", "HEAD"));

        var result = await ServiceFor(hub).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Released, result.WorkspaceCleanup.Outcome);

        var after = await hub.ExecutionAsync(executionId);

        Assert.Equal(WorkspaceState.Released, after.WorkspaceState);
        Assert.False(Directory.Exists(worktree));

        // Released, not undone: the commit and the branch it sits on are still there.
        Assert.Equal(ExecutionOutcome.Passed, after.Outcome);
        Assert.Equal(commit, after.ResultCommitSha);
        Assert.Equal(commit, hub.RunGit("rev-parse", after.BranchName));
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task The_real_pipeline_also_releases_after_a_rejection()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var before = await hub.ExecutionAsync(executionId);
        var worktree = before.WorktreePath;
        var commit = before.ResultCommitSha!;

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

        Assert.False(Directory.Exists(worktree));
        Assert.Equal(commit, hub.RunGit("rev-parse", before.BranchName));

        var after = await hub.ExecutionAsync(executionId);
        Assert.Equal(WorkspaceState.Released, after.WorkspaceState);
        Assert.Equal(ExecutionOutcome.Passed, after.Outcome);
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
