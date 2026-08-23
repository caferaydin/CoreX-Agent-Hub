using System.Runtime.Versioning;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Verification through the orchestrator, and the provenance of the baseline it compares
/// against.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationVerificationTests
{
    [Fact]
    public async Task A_failing_required_step_keeps_the_result_commit_and_returns_the_task()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        var report = await RunWithProfileAsync(hub, "trx-broken", exitCode: 1);

        Assert.Equal(ExecutionState.Completed, report.State);
        Assert.Equal(ExecutionOutcome.VerificationFailed, report.Outcome);

        // The commit survives: it is the evidence of what was verified.
        Assert.NotNull(report.ResultCommitSha);
        Assert.Equal(
            report.ResultCommitSha,
            hub.RunGit("rev-parse", report.ResultCommitSha!));

        var steps = await hub.Store.ListExecutionStepsAsync(report.ExecutionId, CancellationToken.None);
        Assert.Equal(ExecutionStepState.Failed, steps.Single(s => s.Kind == ExecutionStepKind.Verify).State);
        Assert.Equal(ExecutionStepState.Skipped, steps.Single(s => s.Kind == ExecutionStepKind.Review).State);

        Assert.Equal(TaskState.Ready, (await hub.TaskAsync()).State);
    }

    [Fact]
    public async Task The_baseline_belongs_to_the_pinned_commit_and_not_to_the_moving_branch()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();
        hub.Mode = ScriptedAgentMode.ApplyPatch;

        // X: what this execution pins.
        var x = hub.RunGit("rev-parse", "HEAD");

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        // Y: what the Hub committed.
        var y = report.ResultCommitSha!;

        // Z: where the branch has since gone.
        File.WriteAllText(Path.Combine(hub.RepositoryPath, "src", "later.cs"), "// later\n");
        hub.RunGit("add", "-A");
        hub.RunGit("commit", "-q", "-m", "later work on main");
        var z = hub.RunGit("rev-parse", "main");

        Assert.NotEqual(x, y);
        Assert.NotEqual(x, z);
        Assert.NotEqual(y, z);

        // The execution's provenance is X, and nothing that happened to the branch
        // afterwards changed it.
        var execution = await hub.ExecutionAsync(report.ExecutionId);
        Assert.Equal(x, execution.BaseCommitSha);
        Assert.Equal(y, execution.ResultCommitSha);

        // The baseline the timeline reports is X's.
        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        var baseline = Assert.Single(events, e => e.EventType == "Baseline");

        Assert.Contains(x[..12], baseline.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(y[..12], baseline.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(z[..12], baseline.Message, StringComparison.Ordinal);

        // The disposable snapshot was cleaned up, and none is left behind.
        Assert.Null(report.SnapshotResidual);

        var snapshots = Path.Combine(hub.WorkspaceRoot, "_snapshots");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateDirectories(snapshots).Any());
    }

    [Fact]
    public async Task The_baseline_is_cached_under_the_pinned_commit()
    {
        await using var hub = await OrchestrationHarness.CreateAsync();

        var x = hub.RunGit("rev-parse", "HEAD");

        var report = await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Passed, report.Outcome);

        // One cache entry, and it is keyed on the commit the execution pinned.
        var cacheDirectory = Path.Combine(hub.DataDirectory, "cache", "verification");
        Assert.True(Directory.Exists(cacheDirectory));

        var events = await hub.Store.ListOrchestrationEventsAsync(report.ExecutionId, CancellationToken.None);
        var diagnostics = string.Join(" | ", events.Select(e => e.EventType + "=" + e.Message));

        var entries = Directory.GetFiles(cacheDirectory, "*.json");
        Assert.True(entries.Length == 1, $"residual={report.SnapshotResidual}; events={diagnostics}");
        var entry = entries[0];
        var json = await File.ReadAllTextAsync(entry);

        Assert.Contains(x, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(report.ResultCommitSha!, json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ExecutionRunReport> RunWithProfileAsync(
        OrchestrationHarness hub,
        string reportKind,
        int exitCode)
    {
        hub.VerificationProfile = OrchestrationHarness.BuildVerificationProfile(
            hub.OrganizationId, hub.RepositoryId, reportKind, exitCode, hub.Clock.GetUtcNow());

        return await hub.Orchestrator().RunAsync(
            hub.TaskId, OrchestrationHarness.ScriptedProfileName, CancellationToken.None);
    }
}
