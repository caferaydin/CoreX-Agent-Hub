using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Pinned, isolated agent worktrees. The developer working tree is never stashed, reset
/// or cleaned.
/// </summary>
public sealed class WorkspacePreparationTests
{
    [Fact]
    public async Task A_workspace_is_created_on_the_accepted_branch_outside_the_repository()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());
        var workspace = result.Workspace!;

        Assert.Equal("agent/HUB-001/1", workspace.BranchName);
        Assert.EndsWith(
            Path.Combine("hub-dev", "hub-sample", "HUB-001-1"),
            workspace.WorkspacePath,
            StringComparison.Ordinal);

        Assert.True(Directory.Exists(workspace.WorkspacePath));
        Assert.StartsWith(fixture.WorkspaceRoot, workspace.WorkspacePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.RepositoryPath, workspace.WorkspacePath, StringComparison.OrdinalIgnoreCase);

        var branch = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "--abbrev-ref", "HEAD");
        Assert.Equal("agent/HUB-001/1", branch);

        var head = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(workspace.BaseCommitSha, head);

        EvidenceLog.Write(
            $"Prepared branch={workspace.BranchName} base={workspace.BaseCommitSha} path={workspace.WorkspacePath}");
    }

    [Fact]
    public async Task The_base_commit_is_pinned_and_a_moving_source_branch_cannot_change_it()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var preflight = await fixture.Manager.PreflightAsync(
            fixture.RepositoryPath, fixture.Root, "main", CancellationToken.None);

        var pinned = preflight.BaseCommitSha!;

        fixture.WriteRepositoryFile("moved.txt", "after pinning");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "moved.txt");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "advance main");

        var advanced = await fixture.RequireGitAsync(fixture.RepositoryPath, "rev-parse", "main");
        Assert.NotEqual(pinned, advanced);

        // A worktree created from the earlier pin still starts exactly there.
        var request = fixture.Request(attempt: 2);
        var path = Path.Combine(fixture.WorkspaceRoot, request.RelativeWorkspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await fixture.RequireGitAsync(
            fixture.RepositoryPath, "worktree", "add", "--no-track", "-b", request.BranchName, path, pinned);

        var headFromPin = await fixture.RequireGitAsync(path, "rev-parse", "HEAD");
        Assert.Equal(pinned, headFromPin);
        Assert.False(File.Exists(Path.Combine(path, "moved.txt")));

        EvidenceLog.Write($"Pinning: pinned={pinned} branchAdvancedTo={advanced} workspaceHead={headFromPin}");
    }

    [Fact]
    public async Task A_dirty_untracked_and_ignored_source_tree_does_not_reach_the_workspace()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("tracked.txt", "committed content");
        fixture.WriteRepositoryFile(".gitignore", ".env");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "tracked.txt", ".gitignore");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add tracked file");

        fixture.WriteRepositoryFile("tracked.txt", "UNCOMMITTED EDIT");
        fixture.WriteRepositoryFile("scratch.txt", "untracked scratch");
        fixture.WriteRepositoryFile(".env", "SECRET=super-secret-value");

        var statusBefore = await fixture.RequireGitAsync(fixture.RepositoryPath, "status", "--porcelain=v2");
        Assert.NotEmpty(statusBefore);

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(result.Succeeded, result.Describe());

        var workspace = result.Workspace!.WorkspacePath;

        Assert.Equal("committed content", File.ReadAllText(Path.Combine(workspace, "tracked.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(workspace, "scratch.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, ".env")));

        var workspaceStatus = await fixture.RequireGitAsync(workspace, "status", "--porcelain=v2");
        Assert.Empty(workspaceStatus);

        var statusAfter = await fixture.RequireGitAsync(fixture.RepositoryPath, "status", "--porcelain=v2");
        Assert.Equal(statusBefore, statusAfter);
        Assert.Equal("UNCOMMITTED EDIT", File.ReadAllText(Path.Combine(fixture.RepositoryPath, "tracked.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(fixture.RepositoryPath, ".env")));

        EvidenceLog.Write("DirtySourceIsolation: dirty edit, untracked file and ignored .env absent from workspace");
    }

    [Fact]
    public async Task A_second_attempt_gets_its_own_branch_and_directory()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var first = await fixture.Manager.PrepareAsync(fixture.Request(attempt: 1), CancellationToken.None);
        var second = await fixture.Manager.PrepareAsync(fixture.Request(attempt: 2), CancellationToken.None);

        Assert.True(first.Succeeded, first.Describe());
        Assert.True(second.Succeeded, second.Describe());
        Assert.Equal("agent/HUB-001/1", first.Workspace!.BranchName);
        Assert.Equal("agent/HUB-001/2", second.Workspace!.BranchName);
        Assert.NotEqual(first.Workspace.WorkspacePath, second.Workspace.WorkspacePath);
    }

    [Fact]
    public async Task Preparing_the_same_attempt_twice_is_refused()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var first = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(first.Succeeded, first.Describe());

        var second = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Contains(
            second.Issues,
            i => i.Code is WorkspaceIssueCode.WorkspacePathAlreadyExists or WorkspaceIssueCode.BranchAlreadyExists);
    }

    [Fact]
    public async Task An_unusable_repository_never_produces_a_workspace()
    {
        using var fixture = GitFixture.Create();
        Directory.CreateDirectory(fixture.RepositoryPath);

        var result = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Workspace);
        Assert.Contains(result.Issues, i => i.Code == WorkspaceIssueCode.RepositoryUnusable);
        Assert.False(Directory.Exists(Path.Combine(fixture.WorkspaceRoot, "hub-dev")));
    }

    /// <summary>Argv, not a shell: spaces and Turkish characters survive literally.</summary>
    [Fact]
    public async Task Paths_with_spaces_and_turkish_characters_work()
    {
        using var fixture = GitFixture.Create("calisma alani ve dogrulama");
        await fixture.InitialiseRepositoryAsync();

        var request = fixture.Request() with
        {
            OrganizationSlug = "kurum sirket",
            RepositorySlug = "depo ığüşöç",
        };

        var result = await fixture.Manager.PrepareAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Describe());
        var workspace = result.Workspace!.WorkspacePath;

        Assert.Contains("kurum sirket", workspace, StringComparison.Ordinal);
        Assert.Contains("depo ığüşöç", workspace, StringComparison.Ordinal);
        Assert.True(Directory.Exists(workspace));

        fixture.WriteWorkspaceFile(workspace, "İstanbul dosyası.txt", "çalışma");
        var capture = await fixture.Manager.CaptureChangesAsync(result.Workspace!, CancellationToken.None);

        Assert.Contains(capture.ChangedPaths, p => p.Contains("stanbul", StringComparison.Ordinal));

        EvidenceLog.Write($"UnicodePath workspace={workspace} changed={string.Join(", ", capture.ChangedPaths)}");
    }
}
