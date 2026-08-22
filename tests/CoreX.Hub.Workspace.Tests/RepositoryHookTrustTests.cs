using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// PHASE-3 SECURITY CLARIFICATION: Hub-controlled hooks configuration must already be
/// effective during the FIRST checkout-capable git command.
///
/// git worktree add invokes post-checkout, so a repository that configures its own
/// core.hooksPath gets to execute code during the very first checkout - before any later
/// hardening step could possibly apply. The control test below reproduces that on the
/// installed git 2.54, and the protected test proves the Hub path prevents it.
///
/// The hook payload is a marker file. Nothing destructive is ever executed.
/// </summary>
public sealed class RepositoryHookTrustTests
{
    [Fact]
    public async Task Control_an_unprotected_worktree_add_executes_the_repository_post_checkout_hook()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallRepositoryPostCheckoutHookAsync();

        Assert.False(fixture.MarkerExists);

        var target = Path.Combine(fixture.WorkspaceRoot, "control");
        var add = await fixture.RawGitAsync(
            fixture.RepositoryPath, "worktree", "add", "--no-track", "-b", "control/1", target, "HEAD");

        Assert.True(add.Succeeded, add.Describe());
        Assert.True(
            fixture.MarkerExists,
            "control case: the repository hook should have executed during an unprotected worktree add");

        EvidenceLog.Write("HookControl: unprotected git worktree add DID execute the repository post-checkout hook");
    }

    [Fact]
    public async Task Hub_workspace_creation_does_not_execute_the_repository_post_checkout_hook()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallRepositoryPostCheckoutHookAsync();
        fixture.ClearMarker();

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());
        Assert.False(
            fixture.MarkerExists,
            "the repository hook must not run during Hub workspace creation");

        EvidenceLog.Write("HookProtected: Hub workspace creation did NOT execute the repository post-checkout hook");
    }

    [Fact]
    public async Task Only_the_hub_hooks_directory_is_effective_inside_the_agent_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallRepositoryPostCheckoutHookAsync();
        fixture.ClearMarker();

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(result.Succeeded, result.Describe());

        var workspace = result.Workspace!;

        var effective = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--get", GitConfigKeys.HooksPath);

        Assert.Equal(
            GitFixture.ToGitPath(workspace.HooksDirectory),
            GitFixture.ToGitPath(effective),
            ignoreCase: true);

        // A later checkout inside the worktree, run WITHOUT the Hub environment, still
        // cannot reach the repository hook: the worktree scoped config is what protects it.
        var checkout = await fixture.RawGitAsync(workspace.WorkspacePath, "checkout", "-q", "-b", "probe/1");
        Assert.True(checkout.Succeeded, checkout.Describe());
        Assert.False(fixture.MarkerExists, "the repository hook must remain unreachable inside the worktree");

        EvidenceLog.Write($"HookIsolation: effective core.hooksPath in worktree = {effective}");
    }

    [Fact]
    public async Task The_main_worktree_keeps_its_own_configuration()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallRepositoryPostCheckoutHookAsync();

        var before = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--get", GitConfigKeys.HooksPath);

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(result.Succeeded, result.Describe());

        var after = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--get", GitConfigKeys.HooksPath);

        Assert.Equal(before, after);
        Assert.DoesNotContain(HubHooks.HooksDirectoryName, after, StringComparison.Ordinal);

        EvidenceLog.Write($"WorktreeConfigIsolation: main worktree hooksPath unchanged = {after}");
    }
}
