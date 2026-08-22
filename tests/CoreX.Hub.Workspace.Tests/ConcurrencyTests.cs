namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Worktree creation and removal for one repository are serialised with the smallest
/// possible primitive: a per repository semaphore. No distributed lock, no Redis, and no
/// dependency on the future global execution concurrency setting.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Concurrent_worktree_creation_in_one_repository_is_serialised_and_deterministic()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var attempts = Enumerable.Range(1, 4).ToArray();

        var results = await Task.WhenAll(attempts.Select(attempt =>
            fixture.Manager.PrepareAsync(fixture.Request(attempt: attempt), CancellationToken.None)));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Describe()));

        var branches = results.Select(r => r.Workspace!.BranchName).OrderBy(b => b, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            attempts.Select(a => $"agent/HUB-001/{a}").OrderBy(b => b, StringComparer.Ordinal),
            branches);

        Assert.All(results, r => Assert.True(Directory.Exists(r.Workspace!.WorkspacePath)));

        var listed = await fixture.RequireGitAsync(fixture.RepositoryPath, "worktree", "list", "--porcelain");
        foreach (var result in results)
        {
            Assert.Contains(
                GitFixture.ToGitPath(result.Workspace!.WorkspacePath),
                GitFixture.ToGitPath(listed),
                StringComparison.OrdinalIgnoreCase);
        }

        EvidenceLog.Write($"Concurrency: {results.Length} concurrent worktree adds on one repository all succeeded");
    }

    [Fact]
    public async Task Different_repositories_are_not_blocked_by_each_other()
    {
        using var first = GitFixture.Create();
        using var second = GitFixture.Create();

        await first.InitialiseRepositoryAsync();
        await second.InitialiseRepositoryAsync();

        var results = await Task.WhenAll(
            first.Manager.PrepareAsync(first.Request(), CancellationToken.None),
            second.Manager.PrepareAsync(second.Request(), CancellationToken.None));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Describe()));
        Assert.NotEqual(results[0].Workspace!.WorkspacePath, results[1].Workspace!.WorkspacePath);
    }

    [Fact]
    public async Task Concurrent_release_of_several_workspaces_is_safe()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = new List<Workspace.PreparedWorkspace>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await fixture.Manager.PrepareAsync(fixture.Request(attempt: attempt), CancellationToken.None);
            Assert.True(result.Succeeded, result.Describe());
            prepared.Add(result.Workspace!);
        }

        var releases = await Task.WhenAll(prepared.Select(w =>
            fixture.Manager.ReleaseAsync(w, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None)));

        Assert.All(releases, r => Assert.Equal(Workspace.WorkspaceReleaseState.Released, r.State));
        Assert.All(prepared, w => Assert.False(Directory.Exists(w.WorkspacePath)));
    }
}
