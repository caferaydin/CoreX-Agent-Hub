using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The worktree scoped half of the git hardening.
///
/// Repository-local configuration is shared and was already inspected. Worktree scoped
/// configuration is not: it lives in .git/worktrees/&lt;name&gt;/config.worktree, git reads
/// it after the shared file so it wins, and one ordinary git command run from inside the
/// managed worktree writes it. The same is true of the working tree's own .gitattributes,
/// which no pinned commit contains.
///
/// Every test here simulates the agent with the same commands an agent would have, and
/// every assertion is behavioural: the marker helper must not run, nothing may be
/// repaired, and no result commit may exist afterwards.
/// </summary>
[Collection(GitHelperCollection.Name)]
public sealed class WorktreeConfigSafetyTests
{
    private const string ProbeDriver = "corexprobe";

    /// <summary>
    /// Control. Without the inspection this file is about, a filter driver written into
    /// worktree scope and named by an untracked .gitattributes really does execute inside
    /// an ordinary Hub-environment git command. The protected tests below are only
    /// meaningful because this one passes.
    /// </summary>
    [Fact]
    public async Task Control_a_worktree_scoped_filter_runs_during_an_unprotected_stage()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        await InstallWorktreeFilterAsync(fixture, workspace.WorkspacePath);
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "content the filter would see");
        fixture.ClearHelperMarker();

        var stage = await fixture.RawGitAsync(workspace.WorkspacePath, "add", "-A");

        Assert.True(stage.Started);
        Assert.True(
            fixture.HelperMarkerExists,
            "control: a worktree scoped clean filter should run during an uninspected git add -A");

        EvidenceLog.Write(
            "ControlWorktreeFilter: worktree scoped filter DID execute during an uninspected git add -A");
    }

    [Fact]
    public async Task A_worktree_scoped_filter_driver_is_detected_and_never_executed()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "content the filter would see");

        // The agent turns hostile in the one scope the shared listing cannot see.
        await InstallWorktreeFilterAsync(fixture, workspace.WorkspacePath);
        fixture.ClearHelperMarker();

        // The scenario is real: the shared scope is still clean and the worktree scope is not.
        var shared = await fixture.RawGitAsync(
            workspace.WorkspacePath, "config", "--local", "--list", "--name-only");
        Assert.DoesNotContain(ProbeDriver, shared.Stdout, StringComparison.OrdinalIgnoreCase);

        var worktreeScope = await fixture.RawGitAsync(
            workspace.WorkspacePath, "config", "--worktree", "--list", "--name-only");
        Assert.Contains("filter." + ProbeDriver, worktreeScope.Stdout, StringComparison.OrdinalIgnoreCase);

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Contains("filter." + ProbeDriver, capture.SafetyRejection!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(capture.ChangedPaths);
        Assert.Empty(capture.GitStatus);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.RepositoryUnsafe, outcome.State);
        Assert.Null(outcome.CommitSha);

        Assert.False(fixture.HelperMarkerExists, "the probe filter must never have been executed by hub git");

        // Nothing was repaired and no commit was created.
        var stillThere = await fixture.RawGitAsync(
            workspace.WorkspacePath, "config", "--worktree", "--get", "filter." + ProbeDriver + ".clean");
        Assert.Equal(fixture.HelperCommand, stillThere.Stdout.Trim());

        var head = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(workspace.BaseCommitSha, head);

        EvidenceLog.Write(
            "Hardened(worktree filter): worktree scoped filter detected before any working tree command; "
            + "probe never ran, config untouched, no result commit");
    }

    [Fact]
    public async Task An_include_directive_in_worktree_scope_is_rejected_by_the_same_authority()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        var included = Path.Combine(fixture.Root, "smuggled.gitconfig");
        File.WriteAllText(included, "[filter \"" + ProbeDriver + "\"]\n\tclean = whatever\n");

        await fixture.RequireGitAsync(
            workspace.WorkspacePath, "config", "--worktree", "include.path", GitFixture.ToGitPath(included));

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Contains("include.path", capture.SafetyRejection!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worktree-scoped", capture.SafetyRejection!, StringComparison.Ordinal);

        EvidenceLog.Write("Hardened(worktree include): " + capture.SafetyRejection);
    }

    [Fact]
    public async Task An_untracked_working_tree_gitattributes_filter_is_refused_before_any_working_tree_command()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        // Nested and untracked: in no commit, so the pinned-commit scan cannot see it.
        fixture.WriteWorkspaceFile(
            workspace.WorkspacePath,
            Path.Combine("src", "deep", ".gitattributes"),
            "* filter=" + ProbeDriver + "\n");

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "content the filter would see");
        fixture.ClearHelperMarker();

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Contains("filter='" + ProbeDriver + "'", capture.SafetyRejection!, StringComparison.Ordinal);
        Assert.Contains(".gitattributes", capture.SafetyRejection!, StringComparison.Ordinal);
        Assert.Empty(capture.ChangedPaths);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.RepositoryUnsafe, outcome.State);
        Assert.False(fixture.HelperMarkerExists);

        // The file is reported, never deleted or rewritten.
        Assert.True(File.Exists(
            Path.Combine(workspace.WorkspacePath, "src", "deep", ".gitattributes")));

        EvidenceLog.Write("Hardened(working tree attributes): " + capture.SafetyRejection);
    }

    [Fact]
    public async Task A_working_tree_gitattributes_naming_lfs_is_refused()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(
            workspace.WorkspacePath, ".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n");

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Contains("LFS", capture.SafetyRejection!, StringComparison.OrdinalIgnoreCase);

        EvidenceLog.Write("Hardened(working tree lfs): " + capture.SafetyRejection);
    }

    /// <summary>
    /// A diff attribute is not a filter attribute. The per command flags already neutralise
    /// diff drivers, so rejecting them here would refuse ordinary repositories for no gain -
    /// and a scan that refused everything would prove nothing about the ones that matter.
    /// </summary>
    [Fact]
    public async Task A_working_tree_gitattributes_without_a_filter_is_not_a_finding()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, ".gitattributes", "* text=auto\n*.bin diff=marker\n");
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "ordinary work");

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.Null(capture.SafetyRejection);
        Assert.Contains("work.txt", capture.ChangedPaths);

        EvidenceLog.Write("Hardened(attribute scan): a diff attribute is not treated as a filter attribute");
    }

    /// <summary>
    /// Removing the extension is how an agent would make the worktree scope unreadable
    /// rather than merely hostile. A scope that cannot be read is not a scope with nothing
    /// in it, and the difference has to survive: the inspection fails closed.
    /// </summary>
    [Fact]
    public async Task Disabling_the_worktree_config_extension_is_refused_rather_than_read_as_empty()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "work that will not be committed");

        await fixture.RequireGitAsync(
            fixture.RepositoryPath,
            "config",
            "--local",
            "--unset",
            GitConfigKeys.WorktreeConfigExtension);

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Empty(capture.ChangedPaths);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.RepositoryUnsafe, outcome.State);
        Assert.Null(outcome.CommitSha);

        var head = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(workspace.BaseCommitSha, head);

        EvidenceLog.Write("Hardened(worktree extension): " + capture.SafetyRejection);
    }

    [Fact]
    public Task Hooks_path_drift_is_refused_and_never_repaired() =>
        AssertDriftIsRefusedAsync(
            GitConfigKeys.HooksPath,
            async (fixture, workspace) =>
            {
                var agentHooks = Path.Combine(fixture.Root, "agent-hooks");
                Directory.CreateDirectory(agentHooks);

                await fixture.RequireGitAsync(
                    workspace.WorkspacePath,
                    "config",
                    "--worktree",
                    GitConfigKeys.HooksPath,
                    GitFixture.ToGitPath(agentHooks));

                return GitFixture.ToGitPath(agentHooks);
            });

    [Fact]
    public Task Push_url_drift_is_refused_and_never_repaired() =>
        AssertDriftIsRefusedAsync(
            GitConfigKeys.PushUrl,
            async (fixture, workspace) =>
            {
                await fixture.RequireGitAsync(
                    workspace.WorkspacePath, "config", "--worktree", "--unset", GitConfigKeys.PushUrl);

                return null;
            });

    [Fact]
    public Task Credential_helper_drift_is_refused_and_never_repaired() =>
        AssertDriftIsRefusedAsync(
            GitConfigKeys.CredentialHelper,
            async (fixture, workspace) =>
            {
                await fixture.RequireGitAsync(
                    workspace.WorkspacePath, "config", "--worktree", "--unset", GitConfigKeys.CredentialHelper);

                return null;
            });

    /// <summary>
    /// One drift scenario: mutate exactly one hardening key the way an agent could, then
    /// walk up to the next trust boundary and assert it refuses, explains, repairs nothing
    /// and commits nothing.
    /// </summary>
    private static async Task AssertDriftIsRefusedAsync(
        string key,
        Func<GitFixture, PreparedWorkspace, Task<string?>> drift)
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "work.txt", "work that will not be committed");
        fixture.ClearHelperMarker();
        HelperInvocationProbe.Clear();

        var expectedAfterDrift = await drift(fixture, workspace);

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.NotNull(capture.SafetyRejection);
        Assert.Contains(key, capture.SafetyRejection!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(capture.ChangedPaths);

        var outcome = await fixture.Manager.CreateResultCommitAsync(
            workspace, "HUB-001", 1, CancellationToken.None);

        Assert.Equal(ResultCommitState.RepositoryUnsafe, outcome.State);
        Assert.Null(outcome.CommitSha);
        Assert.Contains(key, outcome.Detail!, StringComparison.OrdinalIgnoreCase);

        // Not repaired: the drifted value is still exactly what the agent left behind.
        var observed = await fixture.RawGitAsync(
            workspace.WorkspacePath, "config", "--worktree", "--get", key);

        if (expectedAfterDrift is null)
        {
            Assert.False(observed.Succeeded, $"{key} must still be unset, not written back");
        }
        else
        {
            Assert.Equal(expectedAfterDrift, observed.Stdout.Trim());
        }

        // No result commit, and no helper of any kind ran on the way to refusing.
        var head = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(workspace.BaseCommitSha, head);
        Assert.False(fixture.HelperMarkerExists);
        Assert.False(HelperInvocationProbe.WasInvoked);

        EvidenceLog.Write($"Hardened(drift {key}): refused, not repaired - {capture.SafetyRejection}");
    }

    /// <summary>Writes the probe driver into worktree scope and names it from an untracked file.</summary>
    private static async Task InstallWorktreeFilterAsync(GitFixture fixture, string workspacePath)
    {
        await fixture.RequireGitAsync(
            workspacePath, "config", "--worktree", "filter." + ProbeDriver + ".clean", fixture.HelperCommand);

        await fixture.RequireGitAsync(
            workspacePath, "config", "--worktree", "filter." + ProbeDriver + ".smudge", fixture.HelperCommand);

        fixture.WriteWorkspaceFile(workspacePath, ".gitattributes", "*.txt filter=" + ProbeDriver + "\n");
    }
}
