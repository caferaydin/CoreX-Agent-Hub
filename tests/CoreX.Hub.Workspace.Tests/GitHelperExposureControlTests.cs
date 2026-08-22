namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Control tests. These prove the exposure exists on the installed git before the
/// protections are asserted elsewhere.
///
/// Launching only git.exe is NOT by itself a secondary process boundary: git has several
/// execution surfaces that are not ordinary hooks, and a Hub owned core.hooksPath does
/// nothing about any of them. Every helper here is the deterministic .NET fixture
/// executable and its whole payload is appending a line to a marker file.
/// </summary>
public sealed class GitHelperExposureControlTests
{
    [Fact]
    public async Task Control_a_smudge_filter_runs_during_an_unprotected_checkout()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        fixture.WriteRepositoryFile("data.txt", "filtered content");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "data.txt");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "commit", "-q", "-m", "add filtered file");

        await fixture.InstallFilterDriverAsync();
        fixture.ClearHelperMarker();

        var target = Path.Combine(fixture.WorkspaceRoot, "control-smudge");
        var add = await fixture.RawGitAsync(
            fixture.RepositoryPath, "worktree", "add", "--no-track", "-b", "control/smudge", target, "HEAD");

        Assert.True(add.Succeeded, add.Describe());
        Assert.True(
            fixture.HelperMarkerExists,
            "control: a repository configured smudge filter should run during an unprotected checkout");

        EvidenceLog.Write("ControlSmudge: repository smudge filter DID execute during unprotected worktree add");
    }

    [Fact]
    public async Task Control_a_textconv_helper_runs_during_an_unprotected_diff()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallTextconvDriverAsync();

        fixture.WriteRepositoryFile("payload.bin", "modified payload");
        fixture.ClearHelperMarker();

        var diff = await fixture.RawGitAsync(fixture.RepositoryPath, "diff", "HEAD");

        Assert.True(diff.Started);
        Assert.True(
            fixture.HelperMarkerExists,
            "control: a repository configured textconv helper should run during an unprotected diff");

        EvidenceLog.Write("ControlTextconv: repository textconv helper DID execute during unprotected git diff");
    }

    [Fact]
    public async Task Control_an_external_diff_helper_runs_during_an_unprotected_diff()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallExternalDiffAsync();

        fixture.WriteRepositoryFile("README.md", "# changed for the external diff control");
        fixture.ClearHelperMarker();

        var diff = await fixture.RawGitAsync(fixture.RepositoryPath, "diff", "HEAD");

        Assert.True(diff.Started);
        Assert.True(
            fixture.HelperMarkerExists,
            "control: diff.external should run during an unprotected diff");

        EvidenceLog.Write("ControlExternalDiff: diff.external DID execute during unprotected git diff");
    }

    [Fact]
    public async Task Control_an_fsmonitor_helper_runs_during_an_unprotected_status()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallFsMonitorAsync();

        fixture.WriteRepositoryFile("touched.txt", "index refresh please");
        fixture.ClearHelperMarker();

        // The helper speaks no fsmonitor protocol, so git may well fail afterwards. The
        // security evidence is only that the external program was executed at all.
        var status = await fixture.RawGitAsync(
            fixture.RepositoryPath, TimeSpan.FromSeconds(8), "status", "--porcelain=v2");

        Assert.True(status.Started);
        Assert.True(
            fixture.HelperMarkerExists,
            "control: a repository configured core.fsmonitor should be invoked during an unprotected status");

        EvidenceLog.Write(
            $"ControlFsMonitor: core.fsmonitor helper DID execute during unprotected git status (exit={status.ExitCode})");
    }

    [Fact]
    public async Task Control_a_signing_program_is_invoked_during_an_unprotected_commit()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();
        await fixture.InstallSigningAsync();

        fixture.WriteRepositoryFile("signed.txt", "content for a signed commit");
        await fixture.RequireGitAsync(fixture.RepositoryPath, "add", "signed.txt");
        fixture.ClearHelperMarker();

        // The helper produces no signature, so the commit is expected to fail. What
        // matters is that git tried to run a repository configured program.
        // A short ceiling: the helper produces no signature and git would otherwise wait.
        var commit = await fixture.RawGitAsync(
            fixture.RepositoryPath, TimeSpan.FromSeconds(8), "commit", "-q", "-m", "signed attempt");

        Assert.True(commit.Started);
        Assert.True(
            fixture.HelperMarkerExists,
            "control: a repository requesting signing should invoke the configured gpg program");

        EvidenceLog.Write(
            $"ControlSigning: gpg.program DID execute during unprotected commit (exit={commit.ExitCode})");
    }
}
