namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Errata E1 evidence. Phase 3 detects that HEAD moved away from the pinned base commit
/// and records the SHAs; it never repairs history. Classifying that into an execution
/// outcome is Phase 5 (gate K1).
/// </summary>
public sealed class UnexpectedCommitTests
{
    [Fact]
    public async Task An_agent_created_commit_is_detected_recorded_and_left_completely_untouched()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());
        var workspace = prepared.Workspace!;

        // An agent-like action: commit inside the workspace without asking the Hub.
        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "agent-work.txt", "written and committed by the agent");
        await fixture.RequireGitAsync(workspace.WorkspacePath, "add", "agent-work.txt");
        await fixture.RequireGitAsync(workspace.WorkspacePath, "commit", "-q", "-m", "agent commit");

        var agentSha = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        var reflogBefore = await fixture.RequireGitAsync(workspace.WorkspacePath, "reflog", "--format=%H %gs");

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.True(capture.UnexpectedAgentCommitDetected);
        Assert.Equal(agentSha, capture.CurrentHeadSha);
        Assert.NotEqual(capture.BaseCommitSha, capture.CurrentHeadSha);
        Assert.Contains(agentSha, capture.UnexpectedCommitShas);
        Assert.Contains(agentSha, capture.GitCommitGraph, StringComparison.Ordinal);
        Assert.Contains("agent commit", capture.GitCommitGraph, StringComparison.Ordinal);

        // The commit is still there, still reachable, and history was not rewritten.
        var exists = await fixture.RawGitAsync(workspace.WorkspacePath, "cat-file", "-e", agentSha);
        Assert.True(exists.Succeeded);

        var headAfter = await fixture.RequireGitAsync(workspace.WorkspacePath, "rev-parse", "HEAD");
        Assert.Equal(agentSha, headAfter);

        var ancestor = await fixture.RawGitAsync(
            workspace.WorkspacePath, "merge-base", "--is-ancestor", agentSha, "HEAD");
        Assert.True(ancestor.Succeeded);

        var reflogAfter = await fixture.RequireGitAsync(workspace.WorkspacePath, "reflog", "--format=%H %gs");
        Assert.Equal(reflogBefore, reflogAfter);

        var branchTip = await fixture.RequireGitAsync(
            workspace.WorkspacePath, "rev-parse", workspace.BranchName);
        Assert.Equal(agentSha, branchTip);

        EvidenceLog.Write(
            $"UnexpectedCommit detected sha={agentSha} base={capture.BaseCommitSha} "
            + $"reachableAfterCapture=true reflogUnchanged=true");
    }

    [Fact]
    public async Task Several_agent_commits_are_all_recorded_in_order()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        for (var i = 1; i <= 3; i++)
        {
            fixture.WriteWorkspaceFile(workspace.WorkspacePath, $"step-{i}.txt", $"step {i}");
            await fixture.RequireGitAsync(workspace.WorkspacePath, "add", $"step-{i}.txt");
            await fixture.RequireGitAsync(workspace.WorkspacePath, "commit", "-q", "-m", $"agent step {i}");
        }

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.True(capture.UnexpectedAgentCommitDetected);
        Assert.Equal(3, capture.UnexpectedCommitShas.Count);
        Assert.All(capture.UnexpectedCommitShas, sha => Assert.Equal(40, sha.Length));
    }

    [Fact]
    public async Task No_commit_means_no_detection()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var workspace = prepared.Workspace!;

        fixture.WriteWorkspaceFile(workspace.WorkspacePath, "just-a-file.txt", "no commit was made");

        var capture = await fixture.Manager.CaptureChangesAsync(workspace, CancellationToken.None);

        Assert.False(capture.UnexpectedAgentCommitDetected);
        Assert.Empty(capture.UnexpectedCommitShas);
        Assert.Equal(capture.BaseCommitSha, capture.CurrentHeadSha);
    }
}
