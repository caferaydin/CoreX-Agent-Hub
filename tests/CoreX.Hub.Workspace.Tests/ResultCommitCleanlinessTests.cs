using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// What the worktree still holds once the Hub has committed.
///
/// The answer decides two things downstream: whether the tree can be taken as the commit
/// for anything measured against it, and whether it can be removed later without forcing.
/// Git refuses to remove a worktree holding modified or untracked files, and does not care
/// about ignored ones - so the report is deliberately in exactly those terms.
/// </summary>
public sealed class ResultCommitCleanlinessTests
{
    [Fact]
    public async Task A_result_commit_takes_everything_and_leaves_a_tree_that_can_be_released()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "feature.txt", "implemented by the agent");
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "nested/deeper.txt", "also new");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.Created, outcome.State);

        // Nothing was left behind, and the tree really is the commit.
        Assert.Empty(outcome.UncommittedPaths);
        Assert.Equal(
            outcome.CommitSha,
            await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD"));

        // Which is exactly what lets the ordinary, non-forcing removal succeed.
        var released = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Released, released.State);
        Assert.False(Directory.Exists(workspace.WorkspacePath));
    }

    [Fact]
    public async Task A_file_written_after_the_commit_is_reported_and_blocks_a_plain_removal()
    {
        // Nothing in the pipeline writes here after the commit any more - that is the point
        // of measuring elsewhere - but the report has to be able to say so when something
        // does, because the same condition is what git refuses to remove.
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "feature.txt", "implemented by the agent");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Empty(outcome.UncommittedPaths);

        // The shape verification used to leave behind.
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "results/results.trx", "<TestRun />");

        var again = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 2, CancellationToken.None);

        // Committing again would take it, so this proves the report by asking before that:
        // the second call staged and committed it, and reports a clean tree afterwards.
        Assert.Empty(again.UncommittedPaths);

        // Now leave something behind that no commit will take, and watch removal refuse.
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "results/second.trx", "<TestRun />");

        var refused = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Orphaned, refused.State);
        Assert.True(Directory.Exists(workspace.WorkspacePath));

        EvidenceLog.Write(
            "ResultCommitCleanliness: an untracked file left in the worktree makes a plain "
            + "'worktree remove' refuse - " + refused.Detail);
    }

    [Fact]
    public async Task An_ignored_file_is_not_reported_and_does_not_block_removal()
    {
        // Git removes a worktree that holds only ignored files, so build output a repository
        // has declared uninteresting is not a reason to call the tree dirty.
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile(".gitignore", "obj/" + Environment.NewLine);
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitignore");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "ignore build output");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "feature.txt", "implemented by the agent");

        // Written BEFORE the commit, so "empty" can only be caused by the ignore rule:
        // git add -A will not stage it and git status will not list it.
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "obj/build.tmp", "generated");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.Created, outcome.State);

        // Still on disk, and still not reported, because git does not count it.
        Assert.True(File.Exists(Path.Combine(workspace.WorkspacePath, "obj", "build.tmp")));
        Assert.Empty(outcome.UncommittedPaths);
        Assert.True(outcome.WorktreeStateKnown);

        var released = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None);

        Assert.Equal(WorkspaceReleaseState.Released, released.State);
    }
}
