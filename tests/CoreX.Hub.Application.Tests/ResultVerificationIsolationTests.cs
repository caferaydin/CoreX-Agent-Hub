using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Where the result is measured, and why it matters that it is not the agent's worktree.
///
/// Verification fills a tree with build output. While it ran in the execution worktree the
/// tree was dirty by the time a person decided, and git refuses to remove a dirty worktree
/// - so an accepted run could not be cleaned up without forcing, which the Hub does not do.
/// Measuring a throwaway copy pinned to the Hub's own commit fixes that, and makes the
/// measurement honestly about the commit rather than about a directory that happens to
/// contain it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResultVerificationIsolationTests
{
    private static string SnapshotsRoot(OrchestrationHarness hub) =>
        Path.Combine(hub.WorkspaceRoot, PinnedSnapshot.DirectoryName);

    [Fact]
    public async Task Verification_artifacts_land_in_the_snapshot_and_never_in_the_worktree()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // Watch every tree the engine is handed, and what the profile writes into it.
        var roots = new List<string>();

        var gateway = hub.CreateVerificationGateway(root =>
        {
            roots.Add(root);
            return hub.CreateVerificationEngine(root);
        });

        hub.DecorateVerifier = _ => gateway;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        // Two trees were measured - the result and the baseline - and neither was the
        // agent's worktree. Asserting the count matters: without it, a regression that
        // measured nothing at all would satisfy the two assertions below vacuously.
        Assert.Equal(2, roots.Count);
        Assert.DoesNotContain(roots, r => PathsMatch(r, execution.WorktreePath));
        Assert.All(roots, r => Assert.Contains(PinnedSnapshot.DirectoryName, r, StringComparison.Ordinal));

        // The profile really did produce an artifact - and it is not in the worktree.
        Assert.False(File.Exists(Path.Combine(execution.WorktreePath, "results", "results.trx")));

        // The worktree is exactly the commit: nothing modified, nothing untracked. That is
        // the whole precondition for releasing it without forcing.
        Assert.Equal(string.Empty, hub.RunGitIn(execution.WorktreePath, "status", "--porcelain"));
        Assert.Equal(execution.ResultCommitSha, hub.RunGitIn(execution.WorktreePath, "rev-parse", "HEAD"));

        // Both throwaway trees are gone - the folder that held them remains, which is what
        // proves they were created and then removed rather than never created at all.
        Assert.True(Directory.Exists(SnapshotsRoot(hub)));
        Assert.Empty(Directory.EnumerateDirectories(SnapshotsRoot(hub)));

        Assert.Null(report.SnapshotResidual);
    }

    [Fact]
    public async Task The_result_is_measured_at_the_hub_commit_even_after_the_branch_moves_on()
    {
        // X is where the execution started, Y is what the Hub committed, Z is where the
        // default branch has since gone. The result tree is Y and only Y.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var x = hub.RunGit("rev-parse", "HEAD");

        var measured = new List<string>();

        var gateway = hub.CreateVerificationGateway(root =>
        {
            // What the tree really is, read from git rather than from its name.
            measured.Add(hub.RunGitIn(root, "rev-parse", "HEAD"));
            return hub.CreateVerificationEngine(root);
        });

        ScriptedVerifier? verifier = null;

        hub.DecorateVerifier = _ => verifier = new ScriptedVerifier(gateway)
        {
            // The branch moves on between the commit and the measurement.
            BeforeVerify = () =>
            {
                File.WriteAllText(Path.Combine(hub.RepositoryPath, "src", "later.cs"), "// later");
                hub.RunGit("add", "-A");
                hub.RunGit("commit", "-q", "-m", "later work on main");
            },
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        var y = report.ResultCommitSha!;
        var z = hub.RunGit("rev-parse", "main");

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);
        Assert.NotEqual(x, y);
        Assert.NotEqual(y, z);

        // The verifier was told which commits to measure, and measured exactly those.
        Assert.Equal(x, verifier!.BaseCommitSha);
        Assert.Equal(y, verifier.ResultCommitSha);

        // Two trees, both pinned, neither the worktree.
        Assert.Equal(2, measured.Count);
        Assert.Contains(y, measured);
        Assert.Contains(x, measured);
        Assert.DoesNotContain(z, measured);
    }

    [Fact]
    public async Task A_profile_with_no_required_step_cannot_pass_a_run_that_never_happened()
    {
        // The subtle one. "No required step failed" is vacuously true of a run that did not
        // run, so a profile of only advisory steps could report a commit nobody measured as
        // Passed. Passing has to be a claim about a measurement, not the absence of a
        // failure.
        await using var hub = await OrchestrationHarness.CreateAsync(
            verificationProfile: OptionalOnlyProfile);

        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // Asks for a result at a commit that does not exist, so nothing can be measured.
        hub.DecorateVerifier = inner => new UnmeasurableVerifier(inner);

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);

        // And nothing was recorded as having passed.
        var commands = await hub.Store.ListVerificationCommandsAsync(
            report.ExecutionId, CancellationToken.None);

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.False(c.IsPass));

        // And the baseline record does not invent a score for it. Comparing the baseline
        // against itself would read as "unchanged", which is a claim that this commit scored
        // exactly what the base commit scored - said about a run that produced no counters
        // at all.
        var events = await hub.Store.ListOrchestrationEventsAsync(
            report.ExecutionId, CancellationToken.None);

        var baseline = Assert.Single(events, e => e.EventType == "Baseline");

        Assert.DoesNotContain("Unchanged", baseline.Message, StringComparison.Ordinal);
        Assert.Contains("nothing to compare", baseline.Message, StringComparison.Ordinal);
    }

    /// <summary>A profile whose only step is advisory - legal configuration, and a trap.</summary>
    private static Domain.Catalog.VerificationProfile OptionalOnlyProfile(
        Guid organizationId,
        Guid repositoryId,
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            organizationId,
            repositoryId,
            "fixture-verification",
            true,
            ".",
            [
                new Domain.Catalog.VerificationStep(
                    "test", "fixture", ["report", "results/results.trx", "trx-fixed", "0"],
                    120, required: false, Domain.Catalog.VerificationResultParser.Trx, "results/results.trx"),
            ],
            now);

    /// <summary>Asks for a result at a commit that does not exist, so it can never be measured.</summary>
    private sealed class UnmeasurableVerifier(IExecutionVerifier inner) : IExecutionVerifier
    {
        public Task<VerificationOutcomeView> VerifyAsync(
            Guid executionId,
            Guid repositoryId,
            string baseCommitSha,
            string resultCommitSha,
            CancellationToken ct) =>
            inner.VerifyAsync(executionId, repositoryId, baseCommitSha, new string('e', 40), ct);
    }

    [Fact]
    public async Task A_result_tree_that_moved_while_measuring_cannot_report_a_pass()
    {
        // A command that moved HEAD measured a different commit. Whatever it produced is
        // not this commit's result, so it is thrown away rather than recorded - a false
        // pass would be persisted as machine truth and answered against for ever.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var gateway = hub.CreateVerificationGateway(root =>
            IsResultTree(root)
                ? hub.CreateVerificationEngine(new HeadMovingExecutor(hub, root))
                : hub.CreateVerificationEngine(root));

        hub.DecorateVerifier = _ => gateway;

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        // Never Passed, and the commit it was about is untouched.
        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);
        Assert.Contains("moved while measuring", report.Detail, StringComparison.Ordinal);

        // The steps were recorded as not started rather than silently absent, so a Machine
        // criterion naming one is told why instead of finding nothing.
        var commands = await hub.Store.ListVerificationCommandsAsync(
            report.ExecutionId, CancellationToken.None);

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.False(c.IsPass));
        Assert.All(commands, c => Assert.Equal(VerificationCommandStatus.StartFailed, c.Status));
    }

    [Fact]
    public async Task A_failing_result_leaves_a_worktree_that_could_still_be_released()
    {
        // G.6 says a VerificationFailed worktree is releaseable. It is not released
        // automatically here, but it must be clean enough that it could be - which is only
        // true because the failing run happened somewhere else.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        hub.VerificationProfile = OrchestrationHarness.BuildVerificationProfile(
            hub.OrganizationId, hub.RepositoryId, "trx-broken", 1, hub.Clock.GetUtcNow());

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        Assert.Equal(string.Empty, hub.RunGitIn(execution.WorktreePath, "status", "--porcelain"));
        Assert.Equal(execution.ResultCommitSha, hub.RunGitIn(execution.WorktreePath, "rev-parse", "HEAD"));

        // Not released by any decision, but the workspace authority accepts it: the tree is
        // removable without forcing.
        Assert.Equal(WorkspaceState.Active, execution.WorkspaceState);

        var released = await hub.WorkspaceManager.ReleaseAsync(
            new PreparedWorkspace
            {
                RepositoryPath = hub.RepositoryPath,
                WorkspacePath = execution.WorktreePath,
                BranchName = execution.BranchName,
                BaseCommitSha = execution.BaseCommitSha,
                HooksDirectory = hub.WorkspaceManager.Hooks.HooksDirectory,
                CreatedAtUtc = hub.Clock.GetUtcNow(),
            },
            hasResultCommit: true,
            operatorConfirmed: false,
            CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Released, released.State);
    }

    [Fact]
    public async Task Cancelling_the_result_measurement_leaves_the_worktree_clean()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        using var cancellation = new CancellationTokenSource();

        hub.DecorateVerifier = inner => new ScriptedVerifier(inner)
        {
            BeforeVerify = cancellation.Cancel,
        };

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, cancellation.Token);

        // The accepted Phase 5C semantics, unchanged.
        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);
        Assert.NotNull(report.ResultCommitSha);

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Equal(
            ExecutionStepState.Cancelled,
            steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);

        var execution = await hub.ExecutionAsync(report.ExecutionId);

        // Nothing was measured in the worktree, so nothing was left in it.
        Assert.Equal(string.Empty, hub.RunGitIn(execution.WorktreePath, "status", "--porcelain"));

        // Whatever tree was created for the attempt was disposed of.
        Assert.True(
            !Directory.Exists(SnapshotsRoot(hub)) || !Directory.EnumerateDirectories(SnapshotsRoot(hub)).Any());
    }

    [Fact]
    public async Task A_snapshot_that_will_not_go_away_is_reported_and_changes_nothing()
    {
        // Disposal is housekeeping. A tree that would not go away is said out loud, and the
        // measurement it produced still stands - attributed to the commit it measured.
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // A real reason a tree will not go away on Windows: something still holds a handle
        // inside it. The verification command opens one and keeps it open.
        using var locking = new TreeLockingExecutor(hub.Clock);

        hub.DecorateVerifier = _ => hub.CreateVerificationGateway(root =>
            IsResultTree(root)
                ? hub.CreateVerificationEngine(locking)
                : hub.CreateVerificationEngine(root));

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        // The run produced a result and it is attributed to the commit that was measured;
        // whether a directory could be deleted afterwards does not enter into it.
        Assert.NotNull(report.ResultCommitSha);
        Assert.Equal(ExecutionState.Completed, report.State);

        var commands = await hub.Store.ListVerificationCommandsAsync(
            report.ExecutionId, CancellationToken.None);

        Assert.NotEmpty(commands);

        // And the problem is visible rather than swallowed.
        Assert.NotNull(report.SnapshotResidual);
        Assert.Contains("could not be removed", report.SnapshotResidual!, StringComparison.Ordinal);

        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "SnapshotResidual");

        // The agent's own worktree was not touched to make up for it.
        var execution = await hub.ExecutionAsync(report.ExecutionId);
        Assert.True(Directory.Exists(execution.WorktreePath));
        Assert.Equal(string.Empty, hub.RunGitIn(execution.WorktreePath, "status", "--porcelain"));
    }

    private static bool IsResultTree(string root) =>
        root.Contains("result-", StringComparison.OrdinalIgnoreCase);

    private static bool PathsMatch(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A verification command that leaves a handle open inside the tree it ran in, which is
    /// the ordinary reason a directory will not go away on Windows.
    /// </summary>
    private sealed class TreeLockingExecutor(TimeProvider clock) : IVerificationCommandExecutor, IDisposable
    {
        private FileStream? _held;

        public Task<VerificationCommandResult> ExecuteAsync(
            VerificationCommand command,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            _held ??= new FileStream(
                Path.Combine(command.WorkingDirectory, "held-open.tmp"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);

            var now = clock.GetUtcNow();

            return Task.FromResult(new VerificationCommandResult
            {
                TerminationReason = VerificationTerminationReason.Exited,
                ExitCode = 0,
                StartedAtUtc = now,
                EndedAtUtc = now,
            });
        }

        public void Dispose() => _held?.Dispose();
    }

    /// <summary>
    /// A verification command that moves the tree it was given off its pinned commit, the
    /// way a rogue build script could.
    /// </summary>
    private sealed class HeadMovingExecutor(OrchestrationHarness hub, string root) : IVerificationCommandExecutor
    {
        public Task<VerificationCommandResult> ExecuteAsync(
            VerificationCommand command,
            CancellationToken cancellationToken)
        {
            hub.RunGitIn(root, "checkout", "--detach", "HEAD~1");

            var now = hub.Clock.GetUtcNow();

            return Task.FromResult(new VerificationCommandResult
            {
                TerminationReason = VerificationTerminationReason.Exited,
                ExitCode = 0,
                StartedAtUtc = now,
                EndedAtUtc = now,
            });
        }
    }
}
