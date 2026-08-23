using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Options = CoreX.Hub.Application.Tests.AcceptanceFixture.ReviewBlockedOptions;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// What happens to the worktree once a person has decided, and what must not.
///
/// A passing execution's work is safely in a commit, so its worktree is disposable and G.6
/// says to remove it. Everything else about that removal is secondary: it happens after the
/// decision is committed, on its own, and if it fails the decision still stands. The two
/// truths are reported separately because they can honestly disagree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceCleanupTests
{
    private const string HumanCriterion = "The wording reads well.";

    private static Task<OrchestrationHarness> HubAsync() =>
        OrchestrationHarness.CreateAsync(humanCriteria: [HumanCriterion]);

    /// <summary>A release port that answers however the test needs, and remembers being asked.</summary>
    private sealed class ScriptedRelease : IExecutionWorkspaceRelease
    {
        public WorkspaceCleanupResult Answer { get; init; } =
            new(WorkspaceCleanupOutcome.Released, "removed");

        public Exception? Throw { get; init; }

        public int Invocations { get; private set; }

        public Guid? RepositoryId { get; private set; }

        public string? ResultCommitShaSeen { get; private set; }

        public Task<WorkspaceCleanupResult> ReleaseAsync(
            Guid repositoryId,
            DomainExecution execution,
            CancellationToken ct)
        {
            Invocations++;
            RepositoryId = repositoryId;
            ResultCommitShaSeen = execution.ResultCommitSha;

            return Throw is { } failure
                ? throw failure
                : Task.FromResult(Answer);
        }
    }

    private static AcceptanceService ServiceWith(OrchestrationHarness hub, IExecutionWorkspaceRelease release) =>
        new(hub.Store, new VerificationTruthReader(hub.Store), release);

    private static async Task<AcceptanceRequest> AcceptRequestAsync(OrchestrationHarness hub) => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = 1,
        Actor = "cafer",
        HumanVerdicts = await AcceptanceFixture.HumanVerdictsAsync(hub),
    };

    private static TaskDecisionRequest DecisionRequest() => new()
    {
        TaskKey = "HUB-001",
        AttemptNo = 1,
        Actor = "cafer",
        Reason = "a reason",
    };

    [Fact]
    public async Task Accepting_a_passing_execution_releases_its_worktree()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease();

        var result = await ServiceWith(hub, release).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(WorkspaceCleanupOutcome.Released, result.WorkspaceCleanup.Outcome);
        Assert.Equal(1, release.Invocations);
        Assert.Equal(hub.RepositoryId, release.RepositoryId);

        // Only ever asked for an execution whose work is already in a commit.
        Assert.NotNull(release.ResultCommitShaSeen);

        var execution = await hub.ExecutionAsync(executionId);

        Assert.Equal(WorkspaceState.Released, execution.WorkspaceState);

        // Everything the decision was about is untouched.
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.NotNull(execution.ResultCommitSha);
        Assert.Equal("agent/HUB-001/1", execution.BranchName);
        Assert.Equal(TaskState.Accepted, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task Rejecting_a_passing_execution_releases_its_worktree_too()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease();

        var result = await ServiceWith(hub, release).RejectAsync(
            DecisionRequest(), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(WorkspaceCleanupOutcome.Released, result.WorkspaceCleanup.Outcome);
        Assert.Equal(1, release.Invocations);

        Assert.Equal(TaskState.Rejected, (await hub.TaskAsync()).State);
        Assert.Equal(WorkspaceState.Released, (await hub.ExecutionAsync(executionId)).WorkspaceState);
        Assert.Equal(ExecutionOutcome.Passed, (await hub.ExecutionAsync(executionId)).Outcome);
    }

    [Fact]
    public async Task Sending_the_work_back_keeps_the_worktree()
    {
        // Another iteration was explicitly asked for, so the tree the next attempt may want
        // to look at stays.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease();

        var result = await ServiceWith(hub, release).RequestChangesAsync(
            DecisionRequest(), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(WorkspaceCleanupOutcome.NotAttempted, result.WorkspaceCleanup.Outcome);
        Assert.Equal(0, release.Invocations);

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
        Assert.Equal(WorkspaceState.Active, (await hub.ExecutionAsync(executionId)).WorkspaceState);
    }

    [Fact]
    public async Task A_worktree_that_would_not_go_away_does_not_unmake_the_decision()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease
        {
            Answer = new WorkspaceCleanupResult(
                WorkspaceCleanupOutcome.Orphaned, "the worktree is locked by another process"),
        };

        var result = await ServiceWith(hub, release).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        // The decision succeeded. The cleanup did not. Both are said.
        Assert.Equal(AcceptanceDecisionKind.Accept, result.Decision);
        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Orphaned, result.WorkspaceCleanup.Outcome);

        var execution = await hub.ExecutionAsync(executionId);

        // Still Active, because the tree really is still there.
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);
        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task A_cleanup_that_throws_is_reported_and_not_raised()
    {
        // A locked file, a missing git, a broken config: operational facts about a machine,
        // and none of them a verdict about the work. Throwing here would tell a person their
        // decision failed when it is already recorded.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease { Throw = new IOException("the directory is in use") };

        var result = await ServiceWith(hub, release).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.Failed, result.WorkspaceCleanup.Outcome);
        Assert.Contains("the directory is in use", result.WorkspaceCleanup.Detail, StringComparison.Ordinal);

        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }

    [Fact]
    public async Task A_decision_survives_a_failed_cleanup_across_a_restart()
    {
        // The decision transaction closed before cleanup was even attempted, so what is on
        // disk cannot depend on how cleanup went.
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var release = new ScriptedRelease { Throw = new IOException("the directory is in use") };

        await ServiceWith(hub, release).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        var store = await hub.ReopenStoreAsync();

        var task = await store.FindTaskAsync(hub.TaskId, CancellationToken.None);
        Assert.Equal(TaskState.Accepted, task!.State);

        var execution = await store.FindExecutionAsync(executionId, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Passed, execution!.Outcome);
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);

        var decision = await store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None);
        Assert.NotNull(decision);
        Assert.Equal(AcceptanceDecisionKind.Accept, decision!.Decision);
        Assert.Equal("cafer", decision.Actor);
    }

    [Fact]
    public async Task Nothing_without_a_result_commit_is_ever_offered_for_release()
    {
        // D6. The service does not even ask, and the workspace authority would refuse if it
        // did - two independent reasons, because the invariant must not rest on one of them
        // staying true.
        await using var hub = await HubAsync();

        var execution = await AcceptanceFixture.PersistCompletedAsync(hub, new Options
        {
            Outcome = ExecutionOutcome.ScopeViolation,
            TaskState = TaskState.AwaitingAcceptance,
            WithBlockingFinding = false,
        });

        Assert.Null(execution.ResultCommitSha);
        Assert.True(execution.WorktreeMustBePreserved);

        var release = new ScriptedRelease();

        var result = await ServiceWith(hub, release).RejectAsync(
            DecisionRequest(), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Rejected, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.NotAttempted, result.WorkspaceCleanup.Outcome);
        Assert.Equal(0, release.Invocations);

        Assert.Equal(WorkspaceState.Active, (await hub.ExecutionAsync(execution.Id)).WorkspaceState);
    }

    [Fact]
    public async Task Without_a_release_port_a_decision_is_still_recorded_in_full()
    {
        await using var hub = await HubAsync();
        var executionId = await AcceptanceFixture.RunPassingAsync(hub);

        var result = await AcceptanceFixture.Service(hub).AcceptAsync(
            await AcceptRequestAsync(hub), hub.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(TaskState.Accepted, result.TaskState);
        Assert.Equal(WorkspaceCleanupOutcome.NotAttempted, result.WorkspaceCleanup.Outcome);
        Assert.Contains("no workspace release port", result.WorkspaceCleanup.Detail, StringComparison.Ordinal);

        Assert.Equal(1, await hub.Store.CountAcceptanceDecisionsAsync(executionId, CancellationToken.None));
    }
}
