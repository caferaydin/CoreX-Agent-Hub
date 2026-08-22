using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Two independent layers stop publication. Each test isolates one of them so neither can
/// hide a failure in the other. Everything is local: no network, no external remote.
/// </summary>
public sealed class PushBlockingTests
{
    /// <summary>
    /// Layer A. The push destination is a real local bare repository, so the only thing
    /// that can stop the push is the Hub owned pre-push hook.
    /// </summary>
    [Fact]
    public async Task The_hub_pre_push_hook_rejects_a_push_to_a_reachable_local_remote()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var remote = Path.Combine(fixture.Root, "remote.git");
        Directory.CreateDirectory(remote);
        await fixture.RequireGitAsync(remote, "init", "-q", "--bare");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "remote", "add", "origin", GitFixture.ToGitPath(remote));

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        // Test-only: point the push destination at the reachable local remote so the
        // pushurl layer cannot be what stops it. The hook stays exactly as the Hub wrote it.
        await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--worktree", GitConfigKeys.PushUrl, GitFixture.ToGitPath(remote));

        Assert.True(File.Exists(Path.Combine(workspace.HooksDirectory, HubHooks.PrePushHookName)));

        var push = await fixture.RawGitAsync(workspace.WorkspacePath, "push", "origin", "HEAD");

        Assert.False(push.Succeeded, "the Hub pre-push hook must reject the push");
        Assert.Contains("corex-hub", push.Stderr, StringComparison.OrdinalIgnoreCase);

        // Nothing arrived at the remote.
        var remoteRefs = await fixture.RequireGitAsync(remote, "for-each-ref", "--format=%(refname)");
        Assert.Empty(remoteRefs);

        EvidenceLog.Write($"PushLayerA(pre-push hook): exit={push.ExitCode} stderr={push.Stderr.Trim()}");
    }

    /// <summary>
    /// Layer B. The hook is neutralised for this test only, so the unroutable push
    /// destination is the single remaining protection.
    /// </summary>
    [Fact]
    public async Task The_disabled_push_destination_rejects_a_push_even_without_the_hook()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var remote = Path.Combine(fixture.Root, "remote.git");
        Directory.CreateDirectory(remote);
        await fixture.RequireGitAsync(remote, "init", "-q", "--bare");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "remote", "add", "origin", GitFixture.ToGitPath(remote));

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        var configuredPushUrl = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--get", GitConfigKeys.PushUrl);

        Assert.Equal(GitConfigKeys.DisabledPushUrl, configuredPushUrl);

        // Test-only: aim hooks at an empty directory so no hook exists to reject the push.
        var noHooks = Path.Combine(fixture.Root, "no-hooks");
        Directory.CreateDirectory(noHooks);
        await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--worktree", GitConfigKeys.HooksPath, GitFixture.ToGitPath(noHooks));

        var push = await fixture.RawGitAsync(workspace.WorkspacePath, "push", "origin", "HEAD");

        Assert.False(push.Succeeded, "the disabled push destination must reject the push");

        var remoteRefs = await fixture.RequireGitAsync(remote, "for-each-ref", "--format=%(refname)");
        Assert.Empty(remoteRefs);

        EvidenceLog.Write(
            $"PushLayerB(pushurl={configuredPushUrl}): exit={push.ExitCode} stderr={push.Stderr.Trim()}");
    }

    [Fact]
    public async Task Credential_helper_hardening_is_effective_in_the_worktree()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var helper = await fixture.RawGitAsync(
            prepared.Workspace!.WorkspacePath, "config", "--get-all", GitConfigKeys.CredentialHelper);

        // An empty value resets the helper list, so no interactive helper can run.
        Assert.True(string.IsNullOrWhiteSpace(helper.Stdout), helper.Stdout);

        EvidenceLog.Write($"CredentialHelper: effective value is empty (exit={helper.ExitCode})");
    }
}
