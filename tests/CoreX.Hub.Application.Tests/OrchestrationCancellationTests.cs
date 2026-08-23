using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Verification;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Where cancellation is obeyed, and where it is owed something first.
///
/// Cancellation stops work. It does not get to stop the account of work that already
/// happened: a run whose evidence was discarded because someone pressed stop leaves a
/// worktree nobody can explain. So the sections that record what happened run on their own
/// bounded token, and everything that is still real forward work keeps honouring the
/// caller - which is the difference between narrow immunity and a pipeline that has simply
/// stopped listening.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationCancellationTests
{
    [Fact]
    public async Task Cancelling_before_the_result_commit_leaves_evidence_and_no_commit()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        ScriptedWorkspace? scripted = null;

        // The agent has already finished successfully; the caller cancels in the window
        // between that and the commit.
        hub.DecorateWorkspace = inner => scripted = new ScriptedWorkspace(inner)
        {
            BeforeCapture = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        Assert.Equal(ExecutionState.Cancelled, report.State);
        Assert.Null(report.Outcome);
        Assert.Null(report.ResultCommitSha);

        // The commit was never even attempted: cancellation won before it existed, and
        // afterwards it never wins, because the commit cannot be taken back.
        Assert.False(scripted!.ResultCommitAttempted);

        // The mandatory section ran on a live token even though the caller's was cancelled.
        Assert.False(scripted.CaptureSawCancellation);

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.Agent));
        Assert.Equal(ExecutionStepState.Succeeded, State(steps, ExecutionStepKind.CaptureChanges));

        var verify = steps.Single(s => s.Kind == ExecutionStepKind.Verify);
        Assert.Equal(ExecutionStepState.Skipped, verify.State);
        Assert.Equal("CancelledBeforeResultCommit", verify.StateReason);

        Assert.Equal(ExecutionStepState.Skipped, State(steps, ExecutionStepKind.Review));

        // The work itself is still on disk and still described.
        var evidence = await hub.Store.ListEvidenceAsync(report.ExecutionId, CancellationToken.None);
        Assert.Contains(evidence, e => e.Kind == EvidenceKind.GitStatus);
        Assert.Contains(evidence, e => e.Kind == EvidenceKind.GitWorktreeDiff);
        Assert.DoesNotContain(evidence, e => e.Kind == EvidenceKind.GitCommitDiff);

        var execution = await hub.ExecutionAsync(report.ExecutionId);
        Assert.True(execution.WorktreeMustBePreserved);
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);
        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task Cancelling_during_verification_keeps_the_result_commit_that_already_exists()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        ScriptedVerifier? verifier = null;

        hub.DecorateVerifier = inner => verifier = new ScriptedVerifier(inner)
        {
            BeforeVerify = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        // Verification is forward work, so it was given the caller's token and stopped.
        Assert.True(verifier!.VerifySawCancellation);

        // The commit exists, so the execution cannot be Cancelled - that state means no
        // commit was produced. A required step did not succeed, which the existing outcome
        // rule already answers.
        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);

        Assert.NotNull(report.ResultCommitSha);
        Assert.Equal(report.ResultCommitSha, hub.RunGit("rev-parse", report.ResultCommitSha!));

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);

        // "Was stopped" stays distinguishable from "failed" on the timeline.
        Assert.Equal(ExecutionStepState.Cancelled, State(steps, ExecutionStepKind.Verify));
        Assert.Equal(ExecutionStepState.Skipped, State(steps, ExecutionStepKind.Review));

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task Cancelling_while_the_baseline_runs_caches_nothing_and_still_removes_the_snapshot()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");
        var workspace = await PrepareWorktreeAsync(hub);

        using var cancellation = new CancellationTokenSource();

        var interrupted = new CancellingCommandExecutor(cancellation, hub.Clock);

        // The current run is real; only the baseline - which runs in the disposable
        // snapshot, at a different root - is interrupted.
        var gateway = hub.CreateVerificationGateway(root =>
            string.Equals(root, workspace.WorkspacePath, StringComparison.OrdinalIgnoreCase)
                ? hub.CreateVerificationEngine(root)
                : hub.CreateVerificationEngine(interrupted));

        var outcome = await gateway.VerifyAsync(hub.RepositoryId, workspace, x, cancellation.Token);

        Assert.Equal(1, interrupted.Invocations);
        Assert.True(cancellation.IsCancellationRequested);

        // Half a suite is not this commit's score, so nothing was remembered as one.
        Assert.Null(outcome.Baseline);
        Assert.NotNull(outcome.SnapshotResidual);
        Assert.Contains("cancelled", outcome.SnapshotResidual!, StringComparison.OrdinalIgnoreCase);

        var cacheDirectory = Path.Combine(hub.DataDirectory, "cache", "verification");
        Assert.Empty(Directory.GetFiles(cacheDirectory, "*.json"));

        // Disposal is owed regardless of the caller, and it ran on a token of its own.
        var snapshots = Path.Combine(hub.WorkspaceRoot, "_snapshots");
        Assert.True(
            !Directory.Exists(snapshots) || !Directory.EnumerateDirectories(snapshots).Any(),
            "a cancelled baseline left its snapshot behind");
    }

    [Fact]
    public async Task A_cancelled_baseline_producer_leaves_the_current_run_intact()
    {
        // The current run is measured before the baseline is even asked for, so a baseline
        // that could not be taken removes context, never a verdict.
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");
        var workspace = await PrepareWorktreeAsync(hub);

        using var cancellation = new CancellationTokenSource();

        var gateway = hub.CreateVerificationGateway(root =>
            string.Equals(root, workspace.WorkspacePath, StringComparison.OrdinalIgnoreCase)
                ? hub.CreateVerificationEngine(root)
                : hub.CreateVerificationEngine(new CancellingCommandExecutor(cancellation, hub.Clock)));

        var outcome = await gateway.VerifyAsync(hub.RepositoryId, workspace, x, cancellation.Token);

        Assert.NotNull(outcome.Result);
        Assert.True(outcome.Result!.RequiredPassed);
        Assert.All(outcome.Result.Steps, s => Assert.Equal(VerificationCommandStatus.Passed, s.Status));
    }

    /// <summary>A real hardened worktree, the way an execution would have one.</summary>
    private static async Task<PreparedExecutionWorkspace> PrepareWorktreeAsync(OrchestrationHarness hub)
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

        return new PreparedExecutionWorkspace(
            prepared.Workspace!.WorkspacePath, prepared.Workspace.BranchName, prepared.Workspace.BaseCommitSha);
    }

    private static ExecutionStepState State(IReadOnlyList<ExecutionStep> steps, ExecutionStepKind kind) =>
        steps.Single(s => s.Kind == kind).State;
}
