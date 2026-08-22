using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The whole point of worktree isolation: after a full prepare, edit, capture, commit and
/// release cycle the developer's branch and working tree are exactly as they were.
/// </summary>
public sealed class SourceBranchSafetyTests
{
    [Fact]
    public async Task A_full_cycle_leaves_the_source_branch_and_working_tree_identical()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("tracked.txt", "committed");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "tracked.txt");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "tracked");

        fixture.WriteRepositoryFile("tracked.txt", "DIRTY developer edit");
        fixture.WriteRepositoryFile("scratch.txt", "developer scratch");

        var mainBefore = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");
        var statusBefore = await fixture.RequireGitAsync(fixture.RepositoryPath, "status", "--porcelain=v2");
        var branchBefore = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "--abbrev-ref", "HEAD");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "tracked.txt", "agent edit");
        await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);
        Assert.Equal(ResultCommitState.Created, outcome.State);

        var release = await fixture.Manager.ReleaseAsync(
            workspace, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None);
        Assert.Equal(WorkspaceReleaseState.Released, release.State);

        var mainAfter = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");
        var statusAfter = await fixture.RequireGitAsync(fixture.RepositoryPath, "status", "--porcelain=v2");
        var branchAfter = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "--abbrev-ref", "HEAD");

        Assert.Equal(mainBefore, mainAfter);
        Assert.Equal(statusBefore, statusAfter);
        Assert.Equal(branchBefore, branchAfter);
        Assert.Equal(
            "DIRTY developer edit",
            File.ReadAllText(Path.Combine(fixture.RepositoryPath, "tracked.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(fixture.RepositoryPath, "scratch.txt")));

        // Releasing the worktree does not delete the agent branch.
        var branchStillThere = await fixture.RawGitAsync(
            fixture.RepositoryPath, "rev-parse", "--verify", workspace.BranchName);
        Assert.True(branchStillThere.Succeeded);
        Assert.Equal(outcome.CommitSha, branchStillThere.Stdout.Trim());

        EvidenceLog.Write(
            $"SourceUntouched mainBefore={mainBefore} mainAfter={mainAfter} branchAfterRelease={workspace.BranchName}={outcome.CommitSha}");
    }
}
