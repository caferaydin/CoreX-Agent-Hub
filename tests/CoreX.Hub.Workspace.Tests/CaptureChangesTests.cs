using CoreX.Hub.Workspace;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The Phase 3 evidence primitive. It reports git truth only: no execution outcome is
/// computed, and no scope or secret gate runs here.
/// </summary>
public sealed class CaptureChangesTests
{
    [Fact]
    public async Task An_untracked_new_file_appears_in_the_diff_while_an_ignored_file_does_not()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile(".gitignore", "secrets.env");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", ".gitignore");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add ignore rules");

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "new-file.txt", "created by the agent");
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "secrets.env", "TOKEN=must-never-be-captured");

        // Before capture the new file really is untracked, so the add -N step is what
        // makes it visible rather than the file already being staged.
        var statusBefore = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "status", "--porcelain=v2", "--untracked-files=all");

        Assert.Contains("new-file.txt", statusBefore, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.env", statusBefore, StringComparison.Ordinal);

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.Contains("new-file.txt", capture.GitWorktreeDiff, StringComparison.Ordinal);
        Assert.Contains("created by the agent", capture.GitWorktreeDiff, StringComparison.Ordinal);
        Assert.Contains("new-file.txt", capture.ChangedPaths);

        Assert.DoesNotContain("secrets.env", capture.GitWorktreeDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("must-never-be-captured", capture.GitWorktreeDiff, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.env", capture.ChangedPaths);
        Assert.DoesNotContain(capture.FileInventory, f => f.RelativePath.Contains("secrets", StringComparison.Ordinal));

        EvidenceLog.Write(
            $"CaptureChanges: changed={string.Join(", ", capture.ChangedPaths)} diffChars={capture.GitWorktreeDiff.Length}");
    }

    [Fact]
    public async Task A_modified_tracked_file_is_captured_with_inventory_metadata()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "README.md", "# fixture edited by the agent");

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        var entry = Assert.Single(capture.FileInventory, f => f.RelativePath == "README.md");
        Assert.Equal("M", entry.ChangeStatus);
        Assert.True(entry.Exists);
        Assert.True(entry.SizeBytes > 0);
        Assert.NotNull(entry.Sha256);
        Assert.Equal(64, entry.Sha256!.Length);

        Assert.False(capture.UnexpectedAgentCommitDetected);
        Assert.Empty(capture.UnexpectedCommitShas);
        Assert.Equal(capture.BaseCommitSha, capture.CurrentHeadSha);
        Assert.NotEmpty(capture.GitStatus);

        EvidenceLog.Write(
            $"Inventory README.md status={entry.ChangeStatus} size={entry.SizeBytes} sha256={entry.Sha256}");
    }

    [Fact]
    public async Task A_deleted_file_is_reported_as_absent_in_the_inventory()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        File.Delete(Path.Combine(workspace.WorkspacePath, "README.md"));

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        var entry = Assert.Single(capture.FileInventory, f => f.RelativePath == "README.md");
        Assert.Equal("D", entry.ChangeStatus);
        Assert.False(entry.Exists);
        Assert.Null(entry.Sha256);
    }

    [Fact]
    public async Task A_clean_workspace_reports_no_changes()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);

        Assert.False(capture.HasChanges);
        Assert.Empty(capture.ChangedPaths);
        Assert.Empty(capture.FileInventory);
        Assert.Empty(capture.GitCommitGraph);
    }
}
