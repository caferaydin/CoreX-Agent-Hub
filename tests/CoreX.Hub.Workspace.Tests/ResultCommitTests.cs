using CoreX.Hub.Workspace;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The Hub owned result commit. E.1 #7-A: this is the only production location that
/// invokes git commit. Whether a commit is permitted at all is Phase 5.
/// </summary>
public sealed class ResultCommitTests
{
    [Fact]
    public async Task The_hub_result_commit_succeeds_with_hardened_hooks_and_no_bypass_flags()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallRepositoryPostCheckoutHookAsync();
        fixture.ClearMarker();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "feature.txt", "implemented by the agent");
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "README.md", "# fixture edited");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.Created, outcome.State);
        Assert.NotNull(outcome.CommitSha);
        Assert.Equal(40, outcome.CommitSha!.Length);

        var effectiveHooks = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--get", GitConfigKeys.HooksPath);

        Assert.Equal(
            GitFixture.ToGitPath(workspace.HooksDirectory),
            GitFixture.ToGitPath(effectiveHooks),
            ignoreCase: true);

        var subject = await fixture.RequireGitAsync(workspace.WorkspacePath, "log", "-1", "--format=%s");
        Assert.Equal("agent: HUB-001 attempt 1", subject);

        var author = await fixture.RequireGitAsync(workspace.WorkspacePath, "log", "-1", "--format=%an <%ae>");
        var committer = await fixture.RequireGitAsync(workspace.WorkspacePath, "log", "-1", "--format=%cn <%ce>");
        Assert.Equal($"{GitEnvironment.HubCommitterName} <{GitEnvironment.HubCommitterEmail}>", author);
        Assert.Equal($"{GitEnvironment.HubCommitterName} <{GitEnvironment.HubCommitterEmail}>", committer);

        var files = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "show", "--name-only", "--format=", outcome.CommitSha);

        Assert.Contains("feature.txt", files, StringComparison.Ordinal);
        Assert.Contains("README.md", files, StringComparison.Ordinal);
        Assert.False(fixture.MarkerExists);

        EvidenceLog.Write(
            $"ResultCommit sha={outcome.CommitSha} subject='{subject}' author='{author}' hooksPath={effectiveHooks}");
    }

    [Fact]
    public async Task An_empty_changeset_never_manufactures_a_commit()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        var before = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.NoChanges, outcome.State);
        Assert.Null(outcome.CommitSha);

        var after = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(before, after);

        var count = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-list", "--count", "HEAD");
        Assert.Equal("1", count);

        EvidenceLog.Write("ResultCommit: empty changeset reported NoChanges and created nothing");
    }

    [Fact]
    public async Task An_ignored_file_alone_is_not_a_changeset()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile(".gitignore", "local.env");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitignore");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "ignore rules");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "local.env", "TOKEN=never-committed");

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.NoChanges, outcome.State);
    }
}
