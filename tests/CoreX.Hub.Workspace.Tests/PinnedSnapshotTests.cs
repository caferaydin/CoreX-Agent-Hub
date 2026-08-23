using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The exact commit snapshot seam: materialise the commit the caller holds, not whatever
/// the default branch happens to point at by the time the call is made.
/// </summary>
public sealed class PinnedSnapshotTests
{
    private static PinnedSnapshotRequest Request(GitFixture fixture, string commitSha, string id = "baseline-1") => new()
    {
        RepositoryPath = fixture.RepositoryPath,
        RepositoryTrustRoot = fixture.Root,
        SnapshotRoot = fixture.WorkspaceRoot,
        CommitSha = commitSha,
        SnapshotId = id,
    };

    [Fact]
    public async Task A_snapshot_reports_the_commit_that_was_requested()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());
        Assert.Equal(commit, result.Snapshot!.CommitSha);
        Assert.Equal(commit, result.Snapshot.ObservedHeadSha);

        var head = await fixture.RequireGitAsync(result.Snapshot.SnapshotPath, "rev-parse", "HEAD");
        Assert.Equal(commit, head);
    }

    [Fact]
    public async Task A_snapshot_stays_at_its_commit_after_the_default_branch_moves()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // X is what an execution would have pinned.
        var x = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        // The world moves on: main advances to Y.
        fixture.WriteRepositoryFile("src/later.txt", "work done after the execution started\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "later work");

        var y = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        Assert.NotEqual(x, y);

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, x), CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());

        // The whole reason this seam exists: a baseline measured here is a measurement of
        // X, not of Y.
        Assert.Equal(x, result.Snapshot!.ObservedHeadSha);
        Assert.Equal(x, await fixture.RequireGitAsync(result.Snapshot.SnapshotPath, "rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(result.Snapshot.SnapshotPath, "src", "later.txt")));

        EvidenceLog.Write(
            $"Snapshot provenance: requested={x[..12]} snapshotHead={result.Snapshot.ObservedHeadSha[..12]} defaultBranch={y[..12]}");
    }

    [Fact]
    public async Task A_snapshot_is_detached_and_creates_no_branch()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        var branchesBefore = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "for-each-ref", "--format=%(refname)", "refs/heads");

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None);

        // Detached: symbolic-ref fails, and the branch list is untouched.
        var symbolic = await fixture.RawGitAsync(result.Snapshot!.SnapshotPath, "symbolic-ref", "-q", "HEAD");
        Assert.NotEqual(0, symbolic.ExitCode);

        var branchesAfter = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "for-each-ref", "--format=%(refname)", "refs/heads");

        Assert.Equal(branchesBefore, branchesAfter);
        Assert.DoesNotContain("baseline", branchesAfter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent/", branchesAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_that_does_not_exist_fails_closed()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, "0123456789abcdef0123456789abcdef01234567"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotIssueCode.CommitNotFound, result.Issues[0].Code);
        // Nothing was materialised: the snapshot folder was not even created.
        Assert.False(Directory.Exists(
            Path.Combine(fixture.WorkspaceRoot, PinnedSnapshot.DirectoryName, "baseline-1")));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("HEAD")]
    [InlineData("origin/main")]
    [InlineData("abc1234")]
    [InlineData("")]
    public async Task Anything_that_is_not_a_full_commit_id_is_refused(string reference)
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, reference), CancellationToken.None);

        // A branch name would let the caller believe it pinned something it did not.
        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotIssueCode.CommitShaMalformed, result.Issues[0].Code);
    }

    [Fact]
    public async Task A_dirty_source_working_tree_does_not_leak_into_the_snapshot()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        // The developer is mid-edit, with uncommitted changes and untracked files.
        fixture.WriteRepositoryFile("src/uncommitted.txt", "half finished work\n");
        fixture.WriteRepositoryFile("scratch.tmp", "personal scratch file\n");

        var result = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());

        // The snapshot is the commit, not the desk it was taken from.
        Assert.False(File.Exists(Path.Combine(result.Snapshot!.SnapshotPath, "src", "uncommitted.txt")));
        Assert.False(File.Exists(Path.Combine(result.Snapshot.SnapshotPath, "scratch.tmp")));

        var status = await fixture.RequireGitAsync(
            result.Snapshot.SnapshotPath, "status", "--porcelain=v2", "--untracked-files=all");

        Assert.Equal(string.Empty, status.Trim());
    }

    [Fact]
    public async Task A_snapshot_lives_apart_from_the_repository_and_from_execution_worktrees()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var snapshot = await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None);

        var path = snapshot.Snapshot!.SnapshotPath;

        Assert.False(
            path.StartsWith(fixture.RepositoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the snapshot was created inside the source repository");

        Assert.False(
            path.StartsWith(prepared.Workspace!.WorkspacePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the snapshot was created inside the execution worktree");

        // And it is recognisably a snapshot rather than an attempt.
        Assert.Contains(PinnedSnapshot.DirectoryName, path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_carries_the_same_git_hardening_as_an_execution_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        var snapshot = (await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None)).Snapshot!;

        var hooks = await fixture.RequireGitAsync(
            snapshot.SnapshotPath, "config", "--worktree", "--get", "core.hooksPath");
        var pushUrl = await fixture.RequireGitAsync(
            snapshot.SnapshotPath, "config", "--worktree", "--get", "remote.origin.pushurl");

        Assert.Equal(fixture.Manager.Hooks.HooksDirectory, hooks);
        Assert.Equal("corex-hub://push-disabled", pushUrl);
    }

    [Fact]
    public async Task A_snapshot_that_moved_off_its_commit_is_reported_as_unpinned()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        var snapshot = (await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None)).Snapshot!;

        // Build output is expected in a snapshot and is not drift.
        File.WriteAllText(Path.Combine(snapshot.SnapshotPath, "build.log"), "generated\n");

        var stillPinned = await fixture.Manager.VerifySnapshotPinAsync(snapshot, CancellationToken.None);
        Assert.True(stillPinned.StillPinned, stillPinned.Detail);

        // Moving HEAD is drift, and results measured afterwards are not this commit's.
        fixture.WriteRepositoryFile("src/another.txt", "another commit\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "another");

        var moved = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        await fixture.RequireGitAsync(snapshot.SnapshotPath, "checkout", "--detach", moved);

        var drifted = await fixture.Manager.VerifySnapshotPinAsync(snapshot, CancellationToken.None);

        Assert.False(drifted.StillPinned);
        Assert.Equal(moved, drifted.ObservedHeadSha);
        Assert.Contains("moved", drifted.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_a_snapshot_removes_it_and_leaves_history_alone()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var commit = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD");
        var snapshot = (await fixture.Manager.PrepareSnapshotAsync(
            Request(fixture, commit), CancellationToken.None)).Snapshot!;

        // Verification would leave build output behind; disposal does not care.
        Directory.CreateDirectory(Path.Combine(snapshot.SnapshotPath, "bin"));
        File.WriteAllText(Path.Combine(snapshot.SnapshotPath, "bin", "app.dll"), "generated\n");

        var release = await fixture.Manager.ReleaseSnapshotAsync(snapshot, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Released, release.State);
        Assert.False(Directory.Exists(snapshot.SnapshotPath));

        // The repository is untouched: same HEAD, same branches, snapshot delisted.
        Assert.Equal(commit, await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "HEAD"));

        var listed = await fixture.RequireGitAsync(fixture.RepositoryPath, "worktree", "list", "--porcelain");
        Assert.DoesNotContain(
            GitFixture.ToGitPath(snapshot.SnapshotPath), GitFixture.ToGitPath(listed), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normal_preparation_still_resolves_the_default_branch_and_creates_the_agent_branch()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var expected = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.True(prepared.Succeeded, prepared.Describe());
        Assert.Equal(expected, prepared.Workspace!.BaseCommitSha);
        Assert.Equal("agent/HUB-001/1", prepared.Workspace.BranchName);

        // Still attached to its own branch, unlike a snapshot.
        var branch = await fixture.RequireGitAsync(
            prepared.Workspace.WorkspacePath, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("agent/HUB-001/1", branch);
    }
}
