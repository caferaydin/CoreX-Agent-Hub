using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Read-only repository inspection. Nothing is fetched, pulled or repaired, and no remote
/// is contacted (Phase 3 brief section 8).
/// </summary>
public sealed class RepositoryPreflightTests
{
    [Fact]
    public async Task A_normal_repository_is_usable_and_the_base_commit_resolves()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.True(result.IsUsable, result.Describe());
        Assert.Empty(result.Issues);
        Assert.Equal("main", result.DefaultBranch);
        Assert.NotNull(result.BaseCommitSha);
        Assert.Equal(40, result.BaseCommitSha!.Length);
        Assert.False(result.IsBare);
        Assert.False(result.HasSubmodules);
        Assert.False(result.UsesGitLfs);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_rejected()
    {
        using var fixture = GitFixture.Create();
        Directory.CreateDirectory(fixture.RepositoryPath);

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.NotAGitRepository);
    }

    [Fact]
    public async Task A_missing_default_branch_is_rejected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync("main");

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "release", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.DefaultBranchMissing);
        Assert.Null(result.BaseCommitSha);
    }

    [Fact]
    public async Task A_repository_outside_its_trust_root_is_rejected()
    {
        using var fixture = GitFixture.Create();
        using var other = GitFixture.Create();
        await other.InitialiseRepositoryAsync();

        var result = await fixture.Manager.PreflightAsync(
            other.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.PathRejected);
    }

    [Fact]
    public async Task A_repository_with_submodules_is_rejected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // A committed .gitmodules file is what marks the repository as using submodules.
        fixture.WriteRepositoryFile(".gitmodules", "[submodule \"vendor\"]" + "\n\tpath = vendor" + "\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitmodules");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add submodule config");

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.True(result.HasSubmodules);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.SubmodulesUnsupported);
    }

    [Fact]
    public async Task A_git_lfs_repository_is_rejected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile(".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text" + "\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitattributes");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add lfs attributes");

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.True(result.UsesGitLfs);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.GitLfsUnsupported);
    }

    [Fact]
    public async Task A_linked_worktree_cannot_be_used_as_a_source_repository()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var result = await fixture.Manager.PreflightAsync(
            prepared.Workspace!.WorkspacePath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.IsAnotherWorktree);
    }

    [Fact]
    public async Task A_repository_that_pins_core_worktree_is_rejected_rather_than_rewritten()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var elsewhere = Path.Combine(fixture.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "core.worktree", GitFixture.ToGitPath(elsewhere));

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.IncompatibleWorktreeConfiguration);
    }
}
