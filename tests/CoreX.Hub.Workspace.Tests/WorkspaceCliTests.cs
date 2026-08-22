using System.Text;
using CoreX.Hub.Cli;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// The bounded Phase 3 operator surface. There is no orchestrator yet, so no execution
/// rows exist and every managed worktree git knows about is legitimately unregistered.
/// The commands report that truth; they never remove anything without --confirm.
/// </summary>
public sealed class WorkspaceCliTests
{
    [Fact]
    public async Task Reconcile_reports_git_truth_and_removes_nothing()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        using var cli = CliHarness.For(fixture);
        var result = await cli.RunAsync("workspace", "reconcile", "--org", "hub-dev", "--repo", "hub-sample");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Orphaned", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("HUB-001-1", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("registered: 0", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("nothing is removed automatically", result.StdOut, StringComparison.Ordinal);

        Assert.True(Directory.Exists(prepared.Workspace!.WorkspacePath));

        EvidenceLog.Write("CLI reconcile: reported orphaned worktree and deleted nothing");
    }

    [Fact]
    public async Task Purge_orphans_without_confirmation_only_lists()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);

        using var cli = CliHarness.For(fixture);
        var result = await cli.RunAsync("workspace", "purge-orphans", "--org", "hub-dev", "--repo", "hub-sample");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("re-run with --confirm", result.StdOut, StringComparison.Ordinal);
        Assert.True(Directory.Exists(prepared.Workspace!.WorkspacePath));
    }

    [Fact]
    public async Task Release_without_confirmation_is_denied_for_a_commit_less_workspace()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        var path = prepared.Workspace!.WorkspacePath;

        using var cli = CliHarness.For(fixture);
        var denied = await cli.RunAsync(
            "workspace", "release", "--org", "hub-dev", "--repo", "hub-sample", "--path", path);

        Assert.Equal(2, denied.ExitCode);
        Assert.Contains("DeniedNoResultCommit", denied.StdOut, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));

        var confirmed = await cli.RunAsync(
            "workspace", "release", "--org", "hub-dev", "--repo", "hub-sample", "--path", path, "--confirm");

        Assert.Equal(0, confirmed.ExitCode);
        Assert.Contains("Released", confirmed.StdOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));

        EvidenceLog.Write("CLI release: denied without --confirm, released with it");
    }

    [Fact]
    public async Task An_unknown_workspace_command_is_a_usage_error()
    {
        using var fixture = GitFixture.Create();
        await fixture.InitialiseRepositoryAsync();

        using var cli = CliHarness.For(fixture);
        var result = await cli.RunAsync("workspace", "nuke", "--org", "hub-dev", "--repo", "hub-sample");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command", result.StdErr, StringComparison.Ordinal);
    }
}

/// <summary>Drives the real CLI dispatch against a fixture repository.</summary>
internal sealed class CliHarness : IDisposable
{
    private CliHarness(string dataDirectory, string configPath)
    {
        DataDirectory = dataDirectory;
        ConfigPath = configPath;
    }

    public string DataDirectory { get; }

    public string ConfigPath { get; }

    public static CliHarness For(GitFixture fixture)
    {
        var dataDirectory = Path.Combine(fixture.Root, "hub-data");
        Directory.CreateDirectory(dataDirectory);

        var configPath = Path.Combine(fixture.Root, "hub.config.yaml");
        File.WriteAllText(configPath, BuildConfig(fixture), new UTF8Encoding(false));

        return new CliHarness(dataDirectory, configPath);
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(params string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var full = new List<string> { "--data-dir", DataDirectory, "--config", ConfigPath };
        full.AddRange(arguments);

        var exitCode = await HubCli.RunAsync(full, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static string BuildConfig(GitFixture fixture)
    {
        var root = GitFixture.ToGitPath(fixture.Root);
        var repository = GitFixture.ToGitPath(fixture.RepositoryPath);
        var workspaces = GitFixture.ToGitPath(fixture.WorkspaceRoot);

        return string.Join(
            '\n',
            [
                "schemaVersion: 1",
                "workspace:",
                $"  root: '{workspaces}'",
                $"  fixtureRoot: '{workspaces}'",
                "organizations:",
                "  - slug: hub-dev",
                "    name: Workspace CLI Fixture",
                $"    filesystemRoot: '{root}'",
                "    isolationLevel: Logical",
                "    requiresHardIsolation: false",
                "    environmentAllowlist: [PATH, SystemRoot]",
                "    agentProfiles: []",
                "    projects:",
                "      - slug: hub-sample",
                "        name: Workspace CLI Project",
                "        taskKeyPrefix: HUB",
                "        repositories:",
                "          - slug: hub-sample",
                "            name: hub-sample",
                $"            localPath: '{repository}'",
                "            defaultBranch: main",
                "            protectedBranches: [main]",
                "            verificationProfiles:",
                "              - name: noop",
                "                isDefault: true",
                "                workingDirectory: '.'",
                "                steps:",
                "                  - name: build",
                "                    executable: dotnet",
                "                    args: ['build']",
                "                    timeoutSeconds: 300",
                "                    required: true",
                "                    resultParser: none",
                string.Empty,
            ]);
    }
}
