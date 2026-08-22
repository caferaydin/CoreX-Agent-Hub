using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// ADR-0016 release rules and the Phase 3 reconciler. Failure becomes visible; it is never
/// forcibly erased, and nothing is ever deleted automatically.
/// </summary>
public sealed class ReleaseAndReconcileTests
{
    [Fact]
    public async Task Automatic_release_is_denied_when_there_is_no_result_commit()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "partial.txt", "half finished work");

        var release = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: false, operatorConfirmed: false, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.DeniedNoResultCommit, release.State);
        Assert.True(Directory.Exists(workspace.WorkspacePath));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspacePath, "partial.txt")));

        EvidenceLog.Write("Release: commit-less automatic release DENIED and the work is still on disk");
    }

    [Fact]
    public async Task An_explicitly_confirmed_commit_less_release_works_when_the_worktree_is_clean()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        var release = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: false, operatorConfirmed: true, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Released, release.State);
        Assert.False(Directory.Exists(workspace.WorkspacePath));

        EvidenceLog.Write($"Release: confirmed commit-less release succeeded after {release.RemovalAttempts} attempt(s)");
    }

    /// <summary>
    /// A real Windows sharing violation: an open handle inside the worktree stops git from
    /// removing it. The workspace must become visibly orphaned, not be force deleted.
    /// </summary>
    [Fact]
    public async Task A_locked_worktree_is_reported_as_orphaned_and_never_force_deleted()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        var locked = Path.Combine(workspace.WorkspacePath, "locked.bin");
        await using var handle = new FileStream(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await handle.WriteAsync("held open by another process"u8.ToArray());
        await handle.FlushAsync();

        var release = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Orphaned, release.State);
        Assert.True(release.RemovalAttempts >= 2, $"attempts={release.RemovalAttempts}");
        Assert.True(Directory.Exists(workspace.WorkspacePath), "the workspace must survive a failed removal");
        Assert.True(File.Exists(locked));

        EvidenceLog.Write(
            $"Release: locked worktree -> {release.State} after {release.RemovalAttempts} attempts; directory preserved");
    }

    [Fact]
    public async Task Reconcile_reports_healthy_missing_and_orphaned_without_deleting_anything()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var known = await fixture.Manager.PrepareAsync(fixture.Request(attempt: 1), CancellationToken.None);
        var unregistered = await fixture.Manager.PrepareAsync(fixture.Request(attempt: 2), CancellationToken.None);

        Assert.True(known.Succeeded, known.Describe());
        Assert.True(unregistered.Succeeded, unregistered.Describe());

        var vanished = Path.Combine(fixture.WorkspaceRoot, "hub-dev", "hub-sample", "HUB-001-9");

        var report = await fixture.Manager.ReconcileAsync(
            fixture.RepositoryPath,
            [
                new KnownWorkspace(known.Workspace!.WorkspacePath, known.Workspace.BranchName, true),
                new KnownWorkspace(vanished, "agent/HUB-001/9", false),
            ],
            CancellationToken.None);

        Assert.Contains(report.Entries, e => e.Health == WorkspaceHealth.Healthy);
        Assert.Contains(report.Missing, e => e.WorktreePath == vanished);
        Assert.Contains(
            report.Orphaned,
            e => string.Equals(
                e.WorktreePath.Replace('/', Path.DirectorySeparatorChar),
                unregistered.Workspace!.WorkspacePath,
                StringComparison.OrdinalIgnoreCase));

        // Reporting only: the orphan is still exactly where it was.
        Assert.True(Directory.Exists(unregistered.Workspace!.WorkspacePath));
        Assert.True(Directory.Exists(known.Workspace.WorkspacePath));

        EvidenceLog.Write(
            "Reconcile: "
            + string.Join(" | ", report.Entries.Select(e => $"{e.Health}:{Path.GetFileName(e.WorktreePath)}")));
    }

    [Fact]
    public async Task Reconcile_does_not_treat_the_main_worktree_as_an_orphan()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var report = await fixture.Manager.ReconcileAsync(
            fixture.RepositoryPath, [], CancellationToken.None);

        Assert.Empty(report.Entries);
    }
}
