using System.Diagnostics;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Evidence;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Loop Zero criteria 8 to 15: what happens when the agent does something the Hub will not
/// stand behind.
///
/// Every one of these runs the shipped pipeline against a real repository with a real agent
/// process. The shared shape is the important part: no result commit, evidence kept, and the
/// worktree left exactly as the agent left it, because it holds the only copy of the work.
/// </summary>
public sealed class LoopZeroGateTests
{
    private static async Task<(LoopZeroHub Hub, DomainExecution Execution, HubTask Task, CliResult Run)> RunAsync(
        string agentMode,
        AgentTimeouts? timeouts = null)
    {
        var hub = LoopZeroHub.Create(agentMode: agentMode, timeouts: timeouts);
        await hub.SeedReadyTaskAsync();

        var run = await hub.ExecuteAsync("run", "HUB-001");

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        return (hub, execution, task, run);
    }

    private static async Task<string> EvidenceAsync(LoopZeroHub hub, DomainExecution execution, EvidenceKind kind)
    {
        var store = hub.OpenStore();
        var artifacts = await store.ListEvidenceAsync(execution.Id, CancellationToken.None);
        var artifact = artifacts.FirstOrDefault(a => a.Kind == kind);

        Assert.NotNull(artifact);

        var blobs = new EvidenceBlobStore(Path.Combine(hub.DataDirectory, "evidence"));
        return await blobs.ReadTextAsync(artifact.Sha256, CancellationToken.None);
    }

    [Fact]
    public async Task Criterion08_writing_outside_the_allowed_scope_blocks_the_run_and_keeps_the_evidence()
    {
        var (hub, execution, task, run) = await RunAsync("out-of-scope-write");

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal(ExecutionOutcome.ScopeViolation, execution.Outcome);
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);

            Assert.NotEmpty(await EvidenceAsync(hub, execution, EvidenceKind.GitWorktreeDiff));
            Assert.NotEmpty(await EvidenceAsync(hub, execution, EvidenceKind.FileInventory));

            // Neither step ran: there was nothing the Hub was willing to measure.
            var store = hub.OpenStore();
            var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);

            Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
            Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Review).State);

            Assert.True(Directory.Exists(execution.WorktreePath), "the worktree must be preserved");
        }
    }

    [Fact]
    public async Task Criterion09_a_written_secret_blocks_the_run_and_never_reaches_a_stored_blob()
    {
        var (hub, execution, task, run) = await RunAsync("secret-write");

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal(ExecutionOutcome.SecretGuardBlocked, execution.Outcome);
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);
            Assert.True(Directory.Exists(execution.WorktreePath));

            // Every stored blob, not a sample: the raw synthetic secret appears in none of them.
            var blobsRoot = Path.Combine(hub.DataDirectory, "evidence", EvidenceBlobStore.BlobsDirectoryName);
            var blobs = Directory.Exists(blobsRoot)
                ? Directory.EnumerateFiles(blobsRoot, "*", SearchOption.AllDirectories).ToList()
                : [];

            Assert.NotEmpty(blobs);

            foreach (var blob in blobs)
            {
                var text = await File.ReadAllTextAsync(blob);

                foreach (var secret in SyntheticSecretMarkers)
                {
                    Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                }
            }

            // There is no command that would commit this anyway, and no decision that accepts it.
            var help = await hub.CliAsync("--help");
            Assert.DoesNotContain("commit-result", help.Stdout, StringComparison.Ordinal);

            var forced = await hub.CliAsync("accept", "HUB-001#1", "--override", "--reason", "please");
            Assert.NotEqual(0, forced.ExitCode);

            var store = hub.OpenStore();
            Assert.Equal(0, await store.CountAcceptanceDecisionsAsync(execution.Id, CancellationToken.None));
        }
    }

    /// <summary>
    /// The exact synthetic values the fixture writes, spelled out here rather than read from
    /// the fixture, so the assertion is about literal bytes on disk. Nothing in this list is a
    /// credential for anything.
    /// </summary>
    private static readonly string[] SyntheticSecretMarkers =
    [
        "sk-corexSYNTHETIC0000000000000000abcd",
        "ghp_corexSYNTHETIC0000000000000000abcd",
        "AKIACOREXSYNTHETIC00",
        "SyntheticCorexPassw0rd",
        "U1lOVEhFVElDLUNPUkVYLUtFWS1NQVRFUklBTA==",
    ];

    [Fact]
    public async Task Criterion10_an_agent_that_hangs_is_killed_with_its_whole_process_tree()
    {
        var before = FixtureProcessIds();

        var (hub, execution, task, run) = await RunAsync("hang", AgentTimeouts.Impatient);

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal(ExecutionState.TimedOut, execution.State);
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);
            Assert.True(Directory.Exists(execution.WorktreePath));

            // An agent that wrote nothing leaves an empty inventory, and an empty blob is not
            // evidence of anything - so what is asserted is that the capture ran and that the
            // run still recorded something about itself.
            var store = hub.OpenStore();
            var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);

            Assert.Equal(
                ExecutionStepState.Succeeded,
                steps.Single(s => s.Kind == ExecutionStepKind.CaptureChanges).State);

            Assert.NotEmpty(await store.ListEvidenceAsync(execution.Id, CancellationToken.None));

            // Real processes, checked against the operating system rather than assumed.
            var after = FixtureProcessIds();
            Assert.Empty(after.Except(before));
        }
    }

    private static IReadOnlyList<int> FixtureProcessIds()
    {
        var processes = LoopZeroHub.OwnFixtureProcesses();
        var ids = processes.Select(p => p.Id).ToList();

        foreach (var process in processes)
        {
            process.Dispose();
        }

        return ids;
    }

    [Fact]
    public async Task Criterion11_a_crash_after_partial_work_still_captures_what_was_written()
    {
        var (hub, execution, task, run) = await RunAsync("partial-write");

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal(ExecutionState.Failed, execution.State);
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);
            Assert.True(Directory.Exists(execution.WorktreePath));

            var diff = await EvidenceAsync(hub, execution, EvidenceKind.GitWorktreeDiff);
            var inventory = await EvidenceAsync(hub, execution, EvidenceKind.FileInventory);

            Assert.NotEmpty(diff);
            Assert.NotEmpty(inventory);

            // Untracked work shows up in the diff, which is only true because the capture
            // intends every new file into the index first.
            Assert.Contains("diff --git", diff, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Criterion12_and_13_an_agent_commit_is_a_policy_violation_and_its_history_is_left_alone()
    {
        var (hub, execution, task, run) = await RunAsync("unexpected-commit");

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Equal(ExecutionOutcome.RuntimePolicyViolation, execution.Outcome);
            Assert.Null(execution.ResultCommitSha);
            Assert.True(execution.UnexpectedAgentCommitDetected);
            Assert.False(string.IsNullOrWhiteSpace(execution.UnexpectedCommitShas));
            Assert.Equal(TaskState.Ready, task.State);

            var store = hub.OpenStore();
            var steps = await store.ListExecutionStepsAsync(execution.Id, CancellationToken.None);

            Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
            Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Review).State);

            Assert.NotEmpty(await EvidenceAsync(hub, execution, EvidenceKind.GitCommitGraph));

            // 13: the commit the agent made is still there, on the branch, unmodified.
            var recorded = execution.UnexpectedCommitShas!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var sha = recorded[0];

            Assert.True(Directory.Exists(execution.WorktreePath));
            Assert.Equal(sha, hub.RunGitIn(execution.WorktreePath, "rev-parse", "HEAD"));
            Assert.Equal("commit", hub.RunGitIn(execution.WorktreePath, "cat-file", "-t", sha));

            // And nothing repaired history: the reflog of the execution branch has no reset,
            // rebase or amend in it.
            var reflog = hub.RunGitIn(execution.WorktreePath, "reflog", "show", "--date=iso", execution.BranchName);

            foreach (var repair in new[] { "reset", "rebase", "amend", "filter" })
            {
                Assert.DoesNotContain(repair, reflog, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Criterion14_when_the_agent_also_crashed_the_crash_owns_the_outcome()
    {
        var (hub, execution, task, run) = await RunAsync("unexpected-commit-then-crash");

        using (hub)
        {
            Assert.NotEqual(0, run.ExitCode);

            // K0 wins: the runtime's own terminal failure, not the policy violation.
            Assert.Equal(ExecutionState.Failed, execution.State);
            Assert.NotEqual(ExecutionOutcome.RuntimePolicyViolation, execution.Outcome);

            Assert.True(execution.UnexpectedAgentCommitDetected);
            Assert.False(string.IsNullOrWhiteSpace(execution.UnexpectedCommitShas));
            Assert.Null(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);
            Assert.True(Directory.Exists(execution.WorktreePath));
        }
    }

    [Fact]
    public async Task Criterion15_pushing_from_the_worktree_is_refused_while_the_hub_commit_succeeds()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        var run = await hub.ExecuteAsync("run", "HUB-001");
        Assert.True(run.ExitCode == 0, run.Describe());

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

        // The Hub's own commit was made, with no bypass flag of any kind.
        Assert.NotNull(execution.ResultCommitSha);
        Assert.Equal(
            execution.ResultCommitSha,
            hub.RunGitIn(execution.WorktreePath, "rev-parse", "HEAD"));

        // And a push from inside the same worktree is refused by the protections that commit
        // went through - not by the absence of a remote: the push url is disabled on purpose.
        var exitCode = hub.GitExitCode(execution.WorktreePath, "push", "origin", "HEAD");
        Assert.NotEqual(0, exitCode);
    }
}
