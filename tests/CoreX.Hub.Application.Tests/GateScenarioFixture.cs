using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Composition.Gates;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A real repository, a real Phase 3 workspace, a real scripted run and the real Phase 4
/// evidence store - joined only through the narrow capture and gate seam. There is no
/// orchestrator here and no execution lifecycle: the test drives the steps itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GateScenarioFixture : IDisposable
{
    private GateScenarioFixture(string root, string git)
    {
        Root = root;
        Git = git;
        RepositoryPath = Path.Combine(root, "repo");
        WorkspaceRoot = Path.Combine(root, "cx");
        DataDirectory = Path.Combine(root, "data");

        Directory.CreateDirectory(RepositoryPath);
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(DataDirectory);

        EvidenceStore = new EvidenceBlobStore(Path.Combine(DataDirectory, "evidence"));

        Manager = new WorkspaceManager(
            new ProcessGitCommandExecutor(git, [root]),
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = Path.Combine(root, "control") });
    }

    public string Root { get; }

    public string Git { get; }

    public string RepositoryPath { get; }

    public string WorkspaceRoot { get; }

    public string DataDirectory { get; }

    public EvidenceBlobStore EvidenceStore { get; }

    public WorkspaceManager Manager { get; }

    public static string FixtureExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, "corex-fixture.exe");

    public static GateScenarioFixture Create()
    {
        Assert.True(GitToolchain.TryResolve(out var git, out var diagnostic), diagnostic);

        var root = Path.Combine(Path.GetTempPath(), "corex-gate-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        var fixture = new GateScenarioFixture(root, git!);
        fixture.InitialiseRepository();
        return fixture;
    }

    public WorkspaceRequest Request(int attempt = 1) => new()
    {
        RepositoryPath = RepositoryPath,
        RepositoryTrustRoot = Root,
        WorkspaceRoot = WorkspaceRoot,
        OrganizationSlug = "hub-dev",
        RepositorySlug = "hub-sample",
        TaskKey = "HUB-001",
        AttemptNo = attempt,
        DefaultBranch = "main",
    };

    /// <summary>Runs one scripted mode in the prepared workspace and returns what it did.</summary>
    public async Task<AgentRunResult> RunAgentAsync(PreparedWorkspace workspace, ScriptedAgentMode mode)
    {
        var runtime = new ScriptedAgentRuntime(
            new ScriptedAgentOptions { FixtureExecutablePath = FixtureExecutable, Mode = mode },
            new ProcessAgentRunner(WorkspaceRoot, Manager.Hooks.EmptyGlobalConfigPath));

        Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", Git);

        try
        {
            return await runtime.RunAsync(
                new AgentRunRequest
                {
                    WorkingDirectory = workspace.WorkspacePath,
                    SystemPromptFile = Path.Combine(Root, "system.md"),
                    UserPrompt = "do the deterministic thing",
                    PermissionProfile = AgentPermissionProfile.WorkspaceWrite,
                    EnvironmentAllowlist = ["SystemRoot", "TEMP", "TMP", "PATH", "COREX_FIXTURE_GIT"],
                    HardTimeout = TimeSpan.FromSeconds(60),
                    CorrelationId = "HUB-001#1",
                },
                new Progress<AgentEvent>(_ => { }),
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COREX_FIXTURE_GIT", null);
        }
    }

    /// <summary>The narrow seam: workspace capture becomes bounded gate input.</summary>
    public static CapturedChanges ToGateInput(CaptureChangesResult capture) =>
        CapturedChangesMapper.FromCapture(
            capture.BaseCommitSha,
            capture.CurrentHeadSha,
            capture.UnexpectedCommitShas,
            capture.ChangedPaths,
            capture.GitWorktreeDiff);

    public IReadOnlyList<string> BlobFiles() =>
        Directory.Exists(EvidenceStore.BlobsRoot)
            ? [.. Directory.EnumerateFiles(EvidenceStore.BlobsRoot, "*", SearchOption.AllDirectories)]
            : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void InitialiseRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Fixture");
        RunGit("config", "user.email", "fixture@corex.local");

        Directory.CreateDirectory(Path.Combine(RepositoryPath, "src"));
        File.WriteAllText(Path.Combine(RepositoryPath, "src", "existing.cs"), "// base\n");
        File.WriteAllText(Path.Combine(RepositoryPath, "README.md"), "base\n");

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "base");
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Git,
            WorkingDirectory = RepositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
