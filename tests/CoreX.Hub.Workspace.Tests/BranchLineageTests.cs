using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// ADR-0028: the autopilot's one git question - is accepted work on the default branch yet -
/// answered against a real repository, and never by changing it.
/// </summary>
public sealed class BranchLineageTests
{
    [Fact]
    public async Task A_result_is_on_the_branch_only_once_the_operator_has_taken_it_there()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "feature.txt", "agent work");
        await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        var commit = await fixture.Manager.CreateResultCommitAsync(workspace, "HUB-001", 1, CancellationToken.None);
        Assert.Equal(ResultCommitState.Created, commit.State);

        var mainBefore = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");

        Assert.False(await ContainsAsync(fixture, commit.CommitSha!));

        // Asking changed nothing: the question is read-only.
        Assert.Equal(mainBefore, await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main"));

        // The operator's own step, outside the Hub.
        await fixture.RequireGitAsync(fixture.RepositoryPath, "merge", "--ff-only", "-q", workspace.BranchName);

        Assert.True(await ContainsAsync(fixture, commit.CommitSha!));
    }

    [Fact]
    public async Task A_commit_the_repository_does_not_have_is_unknown_rather_than_absent()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        Assert.Null(await ContainsAsync(fixture, new string('d', 40)));
    }

    [Fact]
    public async Task A_repository_outside_the_trust_root_is_not_asked_at_all()
    {
        using var fixture = GitFixture.Create();
        using var elsewhere = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var head = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");

        Assert.Null(await fixture.Manager.BranchContainsAsync(
            fixture.RepositoryPath, elsewhere.Root, "main", head, CancellationToken.None));
    }

    private static Task<bool?> ContainsAsync(GitFixture fixture, string sha) =>
        fixture.Manager.BranchContainsAsync(fixture.RepositoryPath, fixture.Root, "main", sha, CancellationToken.None);
}
