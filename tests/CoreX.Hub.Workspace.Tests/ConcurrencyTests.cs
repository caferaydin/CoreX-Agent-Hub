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

    [Fact]
    public async Task Two_spellings_of_one_repository_take_the_same_lock()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // Same physical repository, two path forms a caller could plausibly supply.
        var upper = fixture.Request(attempt: 1) with { RepositoryPath = fixture.RepositoryPath.ToUpperInvariant() };
        var trailing = fixture.Request(attempt: 2) with
        {
            RepositoryPath = fixture.RepositoryPath + Path.DirectorySeparatorChar,
        };

        Assert.Equal(
            WorkspaceManager.RepositoryLockKey(upper.RepositoryPath),
            WorkspaceManager.RepositoryLockKey(trailing.RepositoryPath),
            StringComparer.OrdinalIgnoreCase);

        var results = await Task.WhenAll(
            fixture.Manager.PrepareAsync(upper, CancellationToken.None),
            fixture.Manager.PrepareAsync(trailing, CancellationToken.None));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Describe()));

        // Both preparations resolved to the same repository, so both are managed by the
        // same lock rather than by two locks that happen to spell it differently.
        Assert.Equal(
            results[0].Workspace!.RepositoryPath,
            results[1].Workspace!.RepositoryPath,
            StringComparer.OrdinalIgnoreCase);

        var listed = await fixture.RequireGitAsync(fixture.RepositoryPath, "worktree", "list", "--porcelain");
        foreach (var result in results)
        {
            Assert.Contains(
                GitFixture.ToGitPath(result.Workspace!.WorkspacePath),
                GitFixture.ToGitPath(listed),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Different_repositories_resolve_to_different_locks()
    {
        using var first = GitFixture.Create();
        using var second = GitFixture.Create();

        await first.InitialiseRepositoryAsync();
        await second.InitialiseRepositoryAsync();

        // No global workspace lock: two repositories are independently operable.
        Assert.NotEqual(
            WorkspaceManager.RepositoryLockKey(first.RepositoryPath),
            WorkspaceManager.RepositoryLockKey(second.RepositoryPath),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Interleaved_preparation_and_release_leave_the_shared_config_intact()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // Three workspaces exist; three more are prepared while these are being released,
        // so worktree add, worktree remove and the shared config writes all overlap.
        var existing = new List<PreparedWorkspace>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var prepared = await fixture.Manager.PrepareAsync(
                fixture.Request(attempt: attempt), CancellationToken.None);
            Assert.True(prepared.Succeeded, prepared.Describe());
            existing.Add(prepared.Workspace!);
        }

        var releases = existing.Select(w => fixture.Manager.ReleaseAsync(
            w, hasResultCommit: true, operatorConfirmed: false, CancellationToken.None));

        var preparations = Enumerable.Range(4, 3).Select(attempt =>
            fixture.Manager.PrepareAsync(fixture.Request(attempt: attempt), CancellationToken.None));

        await Task.WhenAll(Task.WhenAll(releases), Task.WhenAll(preparations));

        // The shared repository config is still readable and still says exactly what the
        // Hub wrote, so no concurrent rewrite tore it.
        var extension = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "--get-all", "extensions.worktreeConfig");
        Assert.Equal("true", extension.Trim());

        // And git still agrees with the Hub about what exists.
        var listed = await fixture.RequireGitAsync(fixture.RepositoryPath, "worktree", "list", "--porcelain");
        foreach (var released in existing)
        {
            Assert.DoesNotContain(
                GitFixture.ToGitPath(released.WorkspacePath),
                GitFixture.ToGitPath(listed),
                StringComparison.OrdinalIgnoreCase);
        }

        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);
        Assert.True(preflight.IsUsable, preflight.Describe());
    }
}
