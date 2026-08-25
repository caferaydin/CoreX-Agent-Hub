using CoreX.Hub.Cli;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Domain.Executions;
using Microsoft.EntityFrameworkCore;

// Phase 2/3 introduced a CoreX.Hub.Execution namespace, so the domain type of the
// same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Reading back what an execution left behind, and refusing to invent what it did not.
///
/// The evidence store is content addressed and everything in it was redacted on the way in,
/// so this path can only ever show what was already safe to keep. What it must not do is
/// fill a gap by looking at the repository as it stands now - that would answer a different
/// question in the same words.
/// </summary>
public sealed class EvidenceCliTests
{
    private const string Diff = "diff --git a/src/thing.cs b/src/thing.cs\n+// the change\n";

    [Fact]
    public async Task Show_lists_the_recorded_artifacts_deterministically()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: true);

        var first = await hub.RunAsync("evidence", "show", "HUB-001#1");
        var second = await hub.RunAsync("evidence", "show", "HUB-001#1");

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);

        Assert.Contains("GitCommitDiff", first.StdOut, StringComparison.Ordinal);
        Assert.Contains("SHA256", first.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_committed_execution_is_described_by_its_commit_diff()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: true);

        var result = await hub.RunAsync("evidence", "show", "HUB-001#1", "--diff");

        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Which artifact is being shown is stated, not implied.
        Assert.Contains("GitCommitDiff", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("the change", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_execution_with_no_commit_is_described_by_its_working_tree()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: false);

        var result = await hub.RunAsync("evidence", "show", "HUB-001#1", "--diff");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("GitWorktreeDiff", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("the change", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_diff_is_reported_rather_than_regenerated()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: true, withDiff: false);

        var result = await hub.RunAsync("evidence", "show", "HUB-001#1", "--diff");

        Assert.Equal(ExitCodes.OperationFailed, result.ExitCode);
        Assert.Contains("not recorded for this execution", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Nothing is regenerated", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_way_to_ask_for_unredacted_evidence()
    {
        // Redaction happens when evidence is written, so nothing raw exists on this path to
        // expose. The absence of a flag is what keeps it that way.
        foreach (var unsafeFlag in new[] { "--raw", "--unredacted", "--unsafe", "--no-redact" })
        {
            Assert.DoesNotContain(unsafeFlag, HubCli.UsageText, StringComparison.OrdinalIgnoreCase);
        }

        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: true);

        // An unknown flag changes nothing about what is shown.
        var result = await hub.RunAsync("evidence", "show", "HUB-001#1", "--diff", "--raw");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("the change", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attempt_that_does_not_exist_is_a_usage_error()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub, withResultCommit: true);

        var missing = await hub.RunAsync("evidence", "show", "HUB-001#9");
        Assert.Equal(ExitCodes.Usage, missing.ExitCode);

        var malformed = await hub.RunAsync("evidence", "show", "HUB-001");
        Assert.Equal(ExitCodes.Usage, malformed.ExitCode);
        Assert.Contains("does not name an execution", malformed.All, StringComparison.Ordinal);
    }

    /// <summary>An execution with one persisted diff blob, written through the real store.</summary>
    private static async Task SeedAsync(TempHub hub, bool withResultCommit, bool withDiff = true)
    {
        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "evidence", "--allow", "src/**", "--human-criterion", "looks right");

        Assert.Equal(0, created.ExitCode);

        await using var db = hub.NewContext();
        var task = await db.Tasks.FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var execution = new DomainExecution(
            Guid.CreateVersion7(), task.OrganizationId, task.Id, 1, new string('a', 40),
            "agent/HUB-001/1", @"D:\cx\_fixtures\hub-dev\hub-sample\HUB-001-1", null, now);

        if (withResultCommit)
        {
            execution.RecordResultCommit(new string('c', 40), now);
        }

        db.Executions.Add(execution);

        if (withDiff)
        {
            var blobs = VerificationComposition.CreateEvidenceStore(hub.DataDirectory);
            var kind = withResultCommit ? EvidenceKind.GitCommitDiff : EvidenceKind.GitWorktreeDiff;
            var stored = await blobs.WriteTextAsync(Diff, kind, "text/plain", CancellationToken.None);

            db.EvidenceArtifacts.Add(new EvidenceArtifact(
                Guid.CreateVersion7(),
                task.OrganizationId,
                execution.Id,
                null,
                kind,
                stored.Sha256,
                stored.SizeBytes,
                "text/plain",
                stored.RedactionCount,
                now));
        }

        await db.SaveChangesAsync();
    }
}
