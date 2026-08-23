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
    public async Task Configuration_that_turns_unsafe_after_the_preflight_stops_the_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("data.txt", "filtered content\n");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "data.txt");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add filtered file");

        // Safe at preflight: the commit is pinned and, in the real sequence, an execution
        // has already been recorded against it by now.
        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.True(preflight.IsUsable, preflight.Describe());

        var x = preflight.BaseCommitSha!;

        // Repository-local configuration is shared mutable state, and this is the window
        // that used to be unguarded: a filter driver installed here would have run during
        // git worktree add, because checking out is itself the dangerous operation.
        await fixture.InstallFilterDriverAsync();
        fixture.ClearHelperMarker();

        var prepared = await fixture.Manager.PrepareAsync(
            fixture.Request(), preflight, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Contains(prepared.Issues, i => i.Code == WorkspaceIssueCode.RepositoryUnusable);
        Assert.Contains(
            RepositoryIssueCode.UnsafeGitConfiguration.ToString(),
            prepared.Describe(),
            StringComparison.Ordinal);

        // Failed before materialisation, not cleaned up after it: nothing was checked out,
        // so nothing the repository configured ever ran.
        Assert.False(fixture.HelperMarkerExists, "no checkout should have happened, so no filter could run");

        var workspacePath = Path.Combine(
            fixture.WorkspaceRoot, "hub-dev", "hub-sample", "HUB-001-1");

        Assert.False(Directory.Exists(workspacePath));

        var branch = await fixture.RawGitAsync(
            fixture.RepositoryPath, "rev-parse", "--verify", "--quiet", "refs/heads/agent/HUB-001/1");

        Assert.False(branch.Succeeded, "no agent branch may exist after a refused preparation");

        // Provenance survived the refusal untouched, and the branch was never re-resolved.
        Assert.Equal(x, preflight.BaseCommitSha);

        // And the same pinned commit still materialises once the repository is safe again:
        // the refusal was about configuration, never about the commit.
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "--unset", "filter.marker.smudge");
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "--unset", "filter.marker.clean");

        var retried = await fixture.Manager.PrepareAsync(
            fixture.Request(), preflight, CancellationToken.None);

        Assert.True(retried.Succeeded, retried.Describe());
        Assert.Equal(x, retried.Workspace!.BaseCommitSha);
        Assert.Equal(x, await fixture.RequireGitAsync(retried.Workspace.WorkspacePath, "rev-parse", "HEAD"));

        EvidenceLog.Write(
            $"TOCTOU: preflight safe at {x[..12]}, filter installed, prepare refused before checkout, "
            + "same commit materialised after the configuration was restored");
    }

    [Fact]
    public async Task An_include_directive_added_after_the_preflight_stops_the_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.True(preflight.IsUsable, preflight.Describe());

        // The second accepted unsafe family: an included file could import anything, so the
        // effective configuration has to be re-read rather than remembered.
        var included = Path.Combine(fixture.Root, "extra.gitconfig");
        File.WriteAllText(included, "[core]" + Environment.NewLine + "    quotePath = true");

        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "include.path", GitFixture.ToGitPath(included));

        var prepared = await fixture.Manager.PrepareAsync(
            fixture.Request(), preflight, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        Assert.Contains(
            RepositoryIssueCode.UnsafeGitConfiguration.ToString(),
            prepared.Describe(),
            StringComparison.Ordinal);

        Assert.False(Directory.Exists(
            Path.Combine(fixture.WorkspaceRoot, "hub-dev", "hub-sample", "HUB-001-1")));
    }

    [Fact]
    public async Task A_worktree_path_that_was_never_created_is_reported_and_never_acted_on()
    {
        // An execution exists before its worktree does, so between the two its recorded
        // path is a placeholder rather than a location. Reconciliation has to survive that
        // without ever treating it as one: it may say "the Hub knows about this and git
        // does not", which is true, but it must never reach the removal path, because only
        // worktrees git actually lists can be orphaned.
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var real = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(real.Succeeded, real.Describe());

        var placeholder = "(not created yet)";

        var report = await fixture.Manager.ReconcileAsync(
            fixture.RepositoryPath,
            [
                new KnownWorkspace(real.Workspace!.WorkspacePath, real.Workspace.BranchName, false),
                new KnownWorkspace(placeholder, "agent/HUB-001/2", false),
            ],
            CancellationToken.None);

        var missing = Assert.Single(report.Missing);
        Assert.Equal(placeholder, missing.WorktreePath);
        Assert.Null(missing.HeadSha);

        // Nothing removable, so nothing that could be removed.
        Assert.Empty(report.Orphaned);
        Assert.DoesNotContain(report.Orphaned, e => e.WorktreePath == placeholder);

        // And it never became a directory on the way through.
        Assert.False(Directory.Exists(placeholder));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, placeholder)));

        // The real workspace beside it is still reported healthy, so the placeholder did
        // not disturb the answer either. git reports its own spelling of the path, so the
        // separators are normalised before comparing.
        var healthy = Assert.Single(report.Entries, e => e.Health == WorkspaceHealth.Healthy);

        Assert.Equal(
            real.Workspace.WorkspacePath.Replace('/', Path.DirectorySeparatorChar),
            healthy.WorktreePath.Replace('/', Path.DirectorySeparatorChar),
            ignoreCase: true);
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
