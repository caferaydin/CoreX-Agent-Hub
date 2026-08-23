using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The seam between resolving a commit and materialising it.
///
/// Preparation used to resolve the default branch itself, which meant a caller that had
/// already recorded "this attempt is based on X" had no way to insist on X: whatever the
/// branch pointed at when the worktree was finally created won. Handing the preflight back
/// closes that window, and these tests are the window being held open on purpose.
/// </summary>
public sealed class PreflightHandoffTests
{
    [Fact]
    public async Task A_branch_that_advances_after_the_preflight_does_not_reach_the_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("src/state.txt", "this is X\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "X");

        // X: what the caller pins, and would already have written down somewhere.
        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.True(preflight.IsUsable, preflight.Describe());

        var x = preflight.BaseCommitSha!;

        // Y: the branch moves on before the worktree is created. Legal, and irrelevant.
        fixture.WriteRepositoryFile("src/state.txt", "this is Y\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "Y");

        var y = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");
        Assert.NotEqual(x, y);

        var prepared = await fixture.Manager.PrepareAsync(
            fixture.Request(), preflight, CancellationToken.None);

        Assert.True(prepared.Succeeded, prepared.Describe());

        var workspace = prepared.Workspace!;

        Assert.Equal(x, workspace.BaseCommitSha);
        Assert.Equal(x, await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD"));
        Assert.Equal(y, await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main"));

        // And the content is X's, not Y's: this is the tree an agent would start from.
        var content = await File.ReadAllTextAsync(Path.Combine(workspace.WorkspacePath, "src", "state.txt"));

        Assert.Contains("this is X", content, StringComparison.Ordinal);
        Assert.DoesNotContain("this is Y", content, StringComparison.Ordinal);

        EvidenceLog.Write(
            $"Preflight handoff: pinned={x[..12]} worktreeHead={x[..12]} defaultBranch={y[..12]}");
    }

    [Fact]
    public async Task A_preflight_from_another_repository_is_refused()
    {
        using var mine = GitFixture.Create("mine");
        using var theirs = GitFixture.Create("theirs");

        await mine.InitialiseRepositoryAsync();
        await theirs.InitialiseRepositoryAsync();

        var foreign = await theirs.Manager.PreflightAsync(
            theirs.RepositoryPath, theirs.Root, "main", CancellationToken.None);

        Assert.True(foreign.IsUsable, foreign.Describe());

        // A commit id alone proves nothing about where it came from, so the handoff is
        // checked rather than trusted: this one names a repository the request does not.
        var prepared = await mine.Manager.PrepareAsync(mine.Request(), foreign, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Contains(prepared.Issues, i => i.Code == WorkspaceIssueCode.RepositoryUnusable);
    }

    [Fact]
    public async Task An_unusable_preflight_never_becomes_a_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "no-such-branch", CancellationToken.None);

        Assert.False(preflight.IsUsable);
        Assert.Null(preflight.BaseCommitSha);

        var prepared = await fixture.Manager.PrepareAsync(
            fixture.Request(defaultBranch: "no-such-branch"), preflight, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Contains(prepared.Issues, i => i.Code == WorkspaceIssueCode.RepositoryUnusable);
    }

    [Fact]
    public async Task A_preflight_for_a_different_branch_is_refused()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        await fixture.RequireGitAsync(fixture.RepositoryPath, "branch", "release");

        // Both branches resolve, so the commit would look perfectly valid. What makes this
        // wrong is that the request and the pin disagree about which branch was asked for.
        var release = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "release", CancellationToken.None);

        Assert.True(release.IsUsable, release.Describe());

        var prepared = await fixture.Manager.PrepareAsync(
            fixture.Request(defaultBranch: "main"), release, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Contains(prepared.Issues, i => i.Code == WorkspaceIssueCode.RepositoryUnusable);
    }
}
