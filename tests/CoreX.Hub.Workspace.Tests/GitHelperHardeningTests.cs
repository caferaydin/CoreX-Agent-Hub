using CoreX.Hub.Workspace;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The protected side of the control tests. Each surface the control suite proved is
/// exploitable is either neutralised per command or causes the repository to be rejected.
/// Every assertion is behavioural: the marker helper must not run.
/// </summary>
[Collection(GitHelperCollection.Name)]
public sealed class GitHelperHardeningTests
{
    [Fact]
    public async Task A_repository_with_a_filter_driver_is_rejected_before_any_checkout()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("data.txt", "filtered content");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "data.txt");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add filtered file");

        await fixture.InstallFilterDriverAsync();
        fixture.ClearHelperMarker();

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Preflight!.Issues, i => i.Code == RepositoryIssueCode.UnsafeGitConfiguration);
        Assert.False(fixture.HelperMarkerExists, "no checkout should have happened, so no filter could run");

        EvidenceLog.Write("Hardened(filter): repository rejected before checkout - " + result.Preflight!.Describe());
    }

    [Fact]
    public async Task A_repository_with_an_include_directive_is_rejected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var included = Path.Combine(fixture.Root, "extra.gitconfig");
        File.WriteAllText(included, "[core]" + Environment.NewLine + "    quotePath = true");

        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "include.path", GitFixture.ToGitPath(included));

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.UnsafeGitConfiguration);

        EvidenceLog.Write("Hardened(include): " + result.Describe());
    }

    [Fact]
    public async Task A_textconv_helper_never_runs_while_the_hub_collects_evidence()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // diff attributes are allowed: the diff flags neutralise them per command.
        fixture.WriteRepositoryFile(".gitattributes", "*.bin diff=marker");
        fixture.WriteRepositoryFile("payload.bin", "original payload");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitattributes", "payload.bin");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add diff attribute");
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "diff.marker.textconv", fixture.HelperCommand);
        await fixture.InstallExternalDiffAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        fixture.WriteWorkspaceFile(prepared.Workspace!.WorkspacePath, "payload.bin", "modified payload");
        fixture.WriteWorkspaceFile(prepared.Workspace.WorkspacePath, "notes.txt", "plain text change");
        fixture.ClearHelperMarker();

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace, CancellationToken.None);

        Assert.Null(capture.SafetyRejection);
        Assert.False(fixture.HelperMarkerExists, "neither textconv nor diff.external may run");

        Assert.Contains("notes.txt", capture.ChangedPaths);
        Assert.Contains("payload.bin", capture.ChangedPaths);
        Assert.Contains("plain text change", capture.GitWorktreeDiff, StringComparison.Ordinal);

        EvidenceLog.Write(
            $"Hardened(diff): textconv and diff.external did not run; changed={string.Join(", ", capture.ChangedPaths)}");
    }

    [Fact]
    public async Task An_fsmonitor_helper_never_runs_during_managed_status()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallFsMonitorAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        fixture.WriteWorkspaceFile(prepared.Workspace!.WorkspacePath, "touched.txt", "index refresh please");
        fixture.ClearHelperMarker();

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace, CancellationToken.None);

        Assert.Null(capture.SafetyRejection);
        Assert.False(fixture.HelperMarkerExists, "core.fsmonitor must be neutralised for managed commands");
        Assert.Contains("touched.txt", capture.ChangedPaths);
        Assert.NotEmpty(capture.GitStatus);

        EvidenceLog.Write("Hardened(fsmonitor): repository core.fsmonitor helper did not run during managed capture");
    }

    [Fact]
    public async Task The_hub_result_commit_never_invokes_a_signing_program()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallSigningAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        fixture.WriteWorkspaceFile(prepared.Workspace!.WorkspacePath, "feature.txt", "work to commit");

        fixture.ClearHelperMarker();
        HelperInvocationProbe.Clear();

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            prepared.Workspace, "HUB-001", 1, CancellationToken.None);

        // The control test showed an unprotected commit fails while trying to sign, so a
        // successful commit is itself evidence that signing was disabled.
        Assert.Equal(ResultCommitState.Created, outcome.State);
        Assert.NotNull(outcome.CommitSha);

        Assert.False(fixture.HelperMarkerExists);
        Assert.False(HelperInvocationProbe.WasInvoked, "the configured signing program must not run");

        var author = await fixture.RequireGitAsync(
            prepared.Workspace.WorkspacePath, "log", "-1", "--format=%an <%ae>");
        // Read from the Hub identity rather than a copied literal, so the assertion cannot
        // drift away from the value the commit actually uses.
        Assert.Equal(
            $"{GitEnvironment.HubCommitterName} <{GitEnvironment.HubCommitterEmail}>",
            author);

        var signature = await fixture.RequireGitAsync(
            prepared.Workspace.WorkspacePath, "log", "-1", "--format=%G?");
        Assert.Equal("N", signature);

        EvidenceLog.Write(
            $"Hardened(signing): commit {outcome.CommitSha} created, signature='{signature}', helper never ran");
    }

    /// <summary>
    /// Configuration inspected when the workspace was created may change afterwards, so
    /// the safety preflight runs again immediately before anything is staged.
    /// </summary>
    [Fact]
    public async Task Unsafe_configuration_added_after_preparation_is_caught_before_staging()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "content the filter would see");

        // Now the repository turns hostile.
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "filter.evil.clean", fixture.HelperCommand);
        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "filter.evil.smudge", fixture.HelperCommand);

        fixture.ClearHelperMarker();

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);
        Assert.NotNull(capture.SafetyRejection);
        Assert.Empty(capture.ChangedPaths);
        Assert.Empty(capture.GitStatus);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.RepositoryUnsafe, outcome.State);
        Assert.Null(outcome.CommitSha);
        Assert.Contains("filter.evil", outcome.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.HelperMarkerExists, "nothing was staged, so no clean filter could run");

        // Nothing was repaired: the unsafe configuration is still exactly where it was.
        var stillThere = await fixture.RequireGitAsync(
            fixture.RepositoryPath, "config", "--local", "--get", "filter.evil.clean");
        Assert.Equal(fixture.HelperCommand, stillThere);

        // And no commit happened.
        var head = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(workspace.BaseCommitSha, head);

        EvidenceLog.Write(
            "Hardened(mutation): filter added after preparation was caught before git add -A; config left untouched");
    }

    [Fact]
    public async Task A_nested_gitattributes_declaring_lfs_is_detected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        // No .gitattributes at the root: only deep inside the tree.
        fixture.WriteRepositoryFile(
            Path.Combine("assets", "media", ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs -text");

        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add nested lfs attributes");

        Assert.False(File.Exists(Path.Combine(fixture.RepositoryPath, ".gitattributes")));

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.True(result.UsesGitLfs);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.GitLfsUnsupported);

        EvidenceLog.Write("Hardened(nested lfs): " + result.Describe());
    }

    [Fact]
    public async Task A_nested_gitattributes_declaring_a_custom_filter_is_rejected()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile(Path.Combine("tools", ".gitattributes"), "*.txt filter=marker");

        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "-A");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add nested custom filter");

        var result = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        Assert.False(result.IsUsable);
        Assert.False(result.UsesGitLfs);
        Assert.Contains(result.Issues, i => i.Code == RepositoryIssueCode.UnsafeGitConfiguration);

        EvidenceLog.Write("Hardened(nested custom filter): " + result.Describe());
    }
}
