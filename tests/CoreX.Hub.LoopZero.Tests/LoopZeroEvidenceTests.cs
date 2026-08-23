using System.Security.Cryptography;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.LoopZero.Tests;

/// <summary>
/// Loop Zero criteria 5, 6, 7, 18 and 19: determinism, identity and where evidence lives.
/// </summary>
public sealed class LoopZeroEvidenceTests
{
    [Fact]
    public async Task Criterion05_the_whole_loop_runs_with_no_ai_and_no_network()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        var run = await hub.ExecuteAsync("run", "HUB-001");
        Assert.True(run.ExitCode == 0, run.Describe());

        var accept = await hub.ExecuteAsync("accept", "HUB-001#1", "--verdict", "3=met");
        Assert.Equal(0, accept.ExitCode);

        // The only runtime this installation declares is the deterministic fixture, and the
        // only executable verification may run is that same fixture. There is no provider
        // credential, no endpoint and no vendor CLI anywhere in the configuration.
        var config = await File.ReadAllTextAsync(hub.ConfigPath);

        Assert.Contains("scriptedRuntimes", config, StringComparison.Ordinal);
        Assert.DoesNotContain("http", config, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", config, StringComparison.OrdinalIgnoreCase);

        // And the shipped binary carries no HTTP client at all: nothing it can do reaches a
        // network, whether or not one happens to be available while these tests run.
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("CoreX.Hub", StringComparison.Ordinal) == true)
            .SelectMany(a => a.GetTypes())
            .Where(t => t.FullName?.Contains("Http", StringComparison.Ordinal) == true)
            .Select(t => t.FullName!)
            .ToList();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Criterion06_two_attempts_at_the_same_task_and_commit_produce_the_same_context_hash()
    {
        // Verification fails on purpose, so the first attempt ends and hands the task back -
        // which is what makes a second attempt at the same base commit possible.
        using var hub = LoopZeroHub.Create(reportKind: "trx-broken", verificationExitCode: 1);
        await hub.SeedReadyTaskAsync();

        var first = await hub.ExecuteAsync("run", "HUB-001");
        Assert.NotEqual(0, first.ExitCode);

        var second = await hub.ExecuteAsync("run", "HUB-001");
        Assert.NotEqual(0, second.ExitCode);

        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var executions = await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None);

        Assert.Equal(2, executions.Count);

        var one = executions.Single(e => e.AttemptNo == 1);
        var two = executions.Single(e => e.AttemptNo == 2);

        // Same semantic content, same base commit, same hash - even though the attempt number,
        // the execution id and the physical workspace path are all different.
        Assert.Equal(one.BaseCommitSha, two.BaseCommitSha);
        Assert.NotNull(one.ContextPackHash);
        Assert.Equal(one.ContextPackHash, two.ContextPackHash);

        Assert.NotEqual(one.Id, two.Id);
        Assert.NotEqual(one.AttemptNo, two.AttemptNo);
        Assert.NotEqual(one.WorktreePath, two.WorktreePath);
    }

    [Fact]
    public async Task Criterion07_a_broken_suite_fails_verification_and_a_fixed_one_passes()
    {
        using (var broken = LoopZeroHub.Create(reportKind: "trx-broken", verificationExitCode: 1))
        {
            await broken.SeedReadyTaskAsync();

            var run = await broken.ExecuteAsync("run", "HUB-001");
            Assert.NotEqual(0, run.ExitCode);

            var store = broken.OpenStore();
            var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
            var execution = Assert.Single(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));

            Assert.Equal(ExecutionOutcome.VerificationFailed, execution.Outcome);

            // The commit is kept: the run produced something, it just did not measure well.
            Assert.NotNull(execution.ResultCommitSha);
            Assert.Equal(TaskState.Ready, task.State);

            var failing = await store.ListVerificationCommandsAsync(execution.Id, CancellationToken.None);
            Assert.Contains(failing, c => c.StepName == "test" && !c.IsPass);
            Assert.Contains(failing, c => c.TestsFailed > 0);
        }

        using var fixedHub = LoopZeroHub.Create();
        await fixedHub.SeedReadyTaskAsync();

        var passing = await fixedHub.ExecuteAsync("run", "HUB-001");
        Assert.True(passing.ExitCode == 0, passing.Describe());

        var fixedStore = fixedHub.OpenStore();
        var fixedTask = Assert.Single(await fixedStore.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var fixedExecution = Assert.Single(
            await fixedStore.ListExecutionsForTaskAsync(fixedTask.Id, CancellationToken.None));

        Assert.Equal(ExecutionOutcome.Passed, fixedExecution.Outcome);

        // The baseline is recorded against the base commit, distinct from the result, and it
        // was a real comparison: both sides produced counters.
        var events = await fixedStore.ListOrchestrationEventsAsync(fixedExecution.Id, CancellationToken.None);
        var baseline = Assert.Single(events, e => e.EventType == "Baseline");

        Assert.Contains(fixedExecution.BaseCommitSha[..12], baseline.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to compare", baseline.Message, StringComparison.Ordinal);
        Assert.Contains("passed", baseline.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Criterion18_the_database_holds_evidence_metadata_and_every_blob_is_a_file_on_disk()
    {
        using var passing = LoopZeroHub.Create();
        await passing.SeedReadyTaskAsync();
        await passing.ExecuteAsync("run", "HUB-001");

        await AssertBlobsResolveAsync(passing);

        using var blocked = LoopZeroHub.Create(agentMode: "out-of-scope-write");
        await blocked.SeedReadyTaskAsync();
        await blocked.ExecuteAsync("run", "HUB-001");

        await AssertBlobsResolveAsync(blocked);
    }

    /// <summary>
    /// Every artifact of every execution in this installation, not one sample: the recorded
    /// hash names a file outside the database, and that file really hashes to it.
    /// </summary>
    private static async Task AssertBlobsResolveAsync(LoopZeroHub hub)
    {
        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));
        var executions = await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None);

        var blobs = new EvidenceBlobStore(Path.Combine(hub.DataDirectory, "evidence"));
        var checkedAny = false;

        foreach (var execution in executions)
        {
            var artifacts = await store.ListEvidenceAsync(execution.Id, CancellationToken.None);

            Assert.NotEmpty(artifacts);

            foreach (var artifact in artifacts)
            {
                var path = blobs.ResolveBlobPath(artifact.Sha256);

                Assert.True(File.Exists(path), $"{artifact.Kind} blob {artifact.Sha256} is not on disk");

                var bytes = await File.ReadAllBytesAsync(path);

                Assert.Equal(artifact.SizeBytes, bytes.LongLength);
                Assert.Equal(
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    artifact.Sha256.ToLowerInvariant());

                checkedAny = true;
            }
        }

        Assert.True(checkedAny, "no evidence artifact was checked");

        // The database is metadata only: no column anywhere holds a blob body.
        var schema = await ReadSchemaAsync(hub);

        Assert.Contains("EvidenceArtifacts", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Body", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("BlobBytes", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Content", schema, StringComparison.Ordinal);
    }

    private static async Task<string> ReadSchemaAsync(LoopZeroHub hub)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={hub.DatabasePath};Mode=ReadOnly");

        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "select sql from sqlite_master where type = 'table' and name = 'EvidenceArtifacts'";

        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }

    [Fact]
    public async Task Criterion19_an_organization_that_declares_isolation_it_does_not_have_cannot_run_anything()
    {
        using var hub = LoopZeroHub.Create();
        await hub.SeedReadyTaskAsync();

        // The organization now declares isolation it does not have. The next command reseeds
        // from configuration, and D5 blocks it.
        hub.DeclareHardIsolation();

        var run = await hub.ExecuteAsync("run", "HUB-001");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("cannot start executions", run.All, StringComparison.OrdinalIgnoreCase);

        // Refused before anything existed: no execution row, no worktree, no agent.
        var store = hub.OpenStore();
        var task = Assert.Single(await store.FindTasksByKeyAsync("HUB-001", CancellationToken.None));

        Assert.Empty(await store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None));
        Assert.Equal(TaskState.Ready, task.State);

        var worktrees = Path.Combine(hub.WorkspaceRoot, "hub-dev");
        Assert.False(Directory.Exists(worktrees), "no workspace should have been prepared");
    }
}
