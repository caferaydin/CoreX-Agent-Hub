namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Whether a snapshot is still the commit it was made from.
///
/// The review depends on this answer twice: once before the reviewer runs, to refuse a tree
/// that was never the result commit, and once after every attempt, to refuse a review of
/// something that changed underneath it. A detector that missed either kind of change would
/// make both checks decorative.
///
/// Both kinds are proved against real git rather than described: a tracked file edited in
/// place, and a file git has never heard of. The second is the one a naive check misses,
/// because a diff against HEAD says nothing about a file that is not in HEAD.
/// </summary>
public sealed class SnapshotIntegrityTests
{
    [Fact]
    public async Task A_fresh_snapshot_is_clean_and_at_the_commit_it_was_asked_for()
    {
        using var fixture = GitFixture.Create();
        var (snapshot, commit) = await SnapshotAsync(fixture);

        var integrity = await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None);

        Assert.NotNull(integrity);
        Assert.True(integrity!.IsClean);
        Assert.Equal(commit, integrity.HeadSha);
        Assert.False(string.IsNullOrWhiteSpace(integrity.TreeSha));
        Assert.Contains("clean", integrity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edited_tracked_file_makes_the_snapshot_no_longer_clean()
    {
        using var fixture = GitFixture.Create();
        var (snapshot, _) = await SnapshotAsync(fixture);

        var before = await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None);

        await File.WriteAllTextAsync(
            Path.Combine(snapshot.SnapshotPath, "README.md"), "# the reviewer wrote here\n");

        var after = await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None);

        Assert.NotNull(after);
        Assert.False(after!.IsClean);

        // HEAD did not move and the commit's tree is unchanged - the difference is entirely
        // in the working copy, which is exactly the shape a reviewer edit takes.
        Assert.Equal(before!.HeadSha, after.HeadSha);
        Assert.Equal(before.TreeSha, after.TreeSha);
        Assert.Contains("README.md", after.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_git_has_never_heard_of_makes_the_snapshot_no_longer_clean()
    {
        using var fixture = GitFixture.Create();
        var (snapshot, _) = await SnapshotAsync(fixture);

        await File.WriteAllTextAsync(
            Path.Combine(snapshot.SnapshotPath, "reviewer-scratch.txt"), "left behind\n");

        var after = await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None);

        Assert.NotNull(after);
        Assert.False(after!.IsClean);
        Assert.Contains("reviewer-scratch.txt", after.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_whose_head_moved_reports_the_commit_it_actually_holds()
    {
        using var fixture = GitFixture.Create();
        var (snapshot, commit) = await SnapshotAsync(fixture);

        await File.WriteAllTextAsync(
            Path.Combine(snapshot.SnapshotPath, "moved.txt"), "a commit made inside the snapshot\n");

        await fixture.RequireGitAsync(snapshot.SnapshotPath, "add", "-A");
        await fixture.RequireGitAsync(snapshot.SnapshotPath, "commit", "-q", "-m", "moved");

        var moved = await fixture.RequireGitAsync(snapshot.SnapshotPath, "rev-parse", "HEAD");

        var after = await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None);

        Assert.NotNull(after);
        Assert.Equal(moved, after!.HeadSha);
        Assert.NotEqual(commit, after.HeadSha);
    }

    [Fact]
    public async Task A_snapshot_that_is_no_longer_there_cannot_be_inspected()
    {
        using var fixture = GitFixture.Create();
        var (snapshot, _) = await SnapshotAsync(fixture);

        await fixture.Manager.ReleaseSnapshotAsync(snapshot, CancellationToken.None);

        // Not an exception and not a clean answer: nothing can be said about a tree that is
        // gone, and the review treats "could not be inspected" as a refusal.
        Assert.Null(await fixture.Manager.InspectSnapshotIntegrityAsync(snapshot, CancellationToken.None));
    }

    /// <summary>
    /// A snapshot root that does not exist is refused rather than created on the way past.
    ///
    /// The root is a trust root: the guard canonicalises it and compares every candidate
    /// against the result, and it cannot do either for a directory that is not there. Stated
    /// here so that whoever composes a snapshot root knows it has to exist first - which the
    /// Phase 7 acceptance learned the expensive way.
    /// </summary>
    [Fact]
    public async Task A_snapshot_root_that_does_not_exist_is_refused()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        var result = await fixture.Manager.PrepareSnapshotAsync(
            new PinnedSnapshotRequest
            {
                RepositoryPath = fixture.RepositoryPath,
                RepositoryTrustRoot = fixture.Root,
                SnapshotRoot = Path.Combine(fixture.WorkspaceRoot, "never-created"),
                CommitSha = commit,
                SnapshotId = "review-missing-root",
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("trust root", result.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(PinnedSnapshot Snapshot, string Commit)> SnapshotAsync(GitFixture fixture)
    {
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        var result = await fixture.Manager.PrepareSnapshotAsync(
            new PinnedSnapshotRequest
            {
                RepositoryPath = fixture.RepositoryPath,
                RepositoryTrustRoot = fixture.Root,
                SnapshotRoot = fixture.WorkspaceRoot,
                CommitSha = commit,
                SnapshotId = "review-integrity",
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());

        return (result.Snapshot!, commit);
    }
}
