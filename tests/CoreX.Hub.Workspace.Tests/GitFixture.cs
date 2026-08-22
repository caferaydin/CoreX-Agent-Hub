using System.Text;
using CoreX.Hub.Cli.Workspace;
using CoreX.Hub.Workspace;
using CoreX.Hub.Workspace.Abstractions;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// A disposable local git playground: a source repository, a workspace root, a Hub control
/// root and, when a test needs one, a local bare remote.
///
/// No network, no real product repository, no company data.
/// </summary>
internal sealed class GitFixture : IDisposable
{
    private GitFixture(string root, string gitExecutablePath)
    {
        Root = root;
        GitExecutablePath = gitExecutablePath;

        RepositoryPath = Path.Combine(root, "repo");
        WorkspaceRoot = Path.Combine(root, "workspaces");
        HubControlRoot = Path.Combine(root, "hub");
        MarkerPath = Path.Combine(root, "HOOK-MARKER");

        Directory.CreateDirectory(RepositoryPath);
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(HubControlRoot);

        Executor = new ProcessGitCommandExecutor(gitExecutablePath, [root]);
        PathGuard = new FileSystemGuardAdapter();

        Manager = new WorkspaceManager(
            Executor,
            PathGuard,
            new WorkspaceOptions
            {
                HubControlRoot = HubControlRoot,
                GitTimeout = TimeSpan.FromSeconds(60),
                RemovalRetryDelay = TimeSpan.FromMilliseconds(100),
            });
    }

    public string Root { get; }

    public string RepositoryPath { get; }

    public string WorkspaceRoot { get; }

    public string HubControlRoot { get; }

    public string MarkerPath { get; }

    public string GitExecutablePath { get; }

    public IGitCommandExecutor Executor { get; }

    public IWorkspacePathGuard PathGuard { get; }

    public WorkspaceManager Manager { get; }

    public static string ResolvedGitPath { get; } = ResolveGit();

    public static GitFixture Create(string? folderName = null)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "corex-ws-tests",
            folderName is null
                ? Guid.CreateVersion7().ToString("N")
                : folderName + "-" + Guid.CreateVersion7().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        var canonical = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root;

        return new GitFixture(canonical, ResolvedGitPath);
    }

    /// <summary>Runs git the way a developer would: no Hub hook override.</summary>
    public async Task<GitCommandResult> RawGitAsync(string workingDirectory, params string[] arguments)
    {
        var environment = GitEnvironment.WithHubIdentity(
            GitEnvironment.Base(Path.Combine(HubControlRoot, HubHooks.GlobalConfigFileName)));

        return await Executor.ExecuteAsync(
            new GitCommand
            {
                WorkingDirectory = workingDirectory,
                Arguments = arguments,
                Environment = environment,
                Timeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);
    }

    public async Task<string> RequireGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RawGitAsync(workingDirectory, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} -> {result.Describe()}");
        return result.Stdout.Trim();
    }

    /// <summary>Initialises the source repository with one commit on the given branch.</summary>
    public async Task InitialiseRepositoryAsync(string defaultBranch = "main")
    {
        // Hub control files must exist before any git command that could run a hook.
        HubHooks.Materialise(HubControlRoot);

        await RequireGitAsync(RepositoryPath, "init", "-q", "-b", defaultBranch);
        WriteRepositoryFile("README.md", "# fixture" + "\n");
        await RequireGitAsync(RepositoryPath, "add", "README.md");
        await RequireGitAsync(RepositoryPath, "commit", "-q", "-m", "init");
    }

    public void WriteRepositoryFile(string relativePath, string content)
    {
        var path = Path.Combine(RepositoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public void WriteWorkspaceFile(string workspacePath, string relativePath, string content)
    {
        var path = Path.Combine(workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// <summary>
    /// Configures the repository to run its own post-checkout hook, which writes a marker
    /// file. Nothing destructive: the marker is the entire payload.
    /// </summary>
    public async Task InstallRepositoryPostCheckoutHookAsync()
    {
        var hooks = Path.Combine(Root, "repo-hooks");
        Directory.CreateDirectory(hooks);

        var script = string.Join(
            '\n',
            [
                "#!/bin/sh",
                "printf 'repository post-checkout executed' > \"" + ToGitPath(MarkerPath) + "\"",
                string.Empty,
            ]);

        File.WriteAllText(Path.Combine(hooks, "post-checkout"), script, new UTF8Encoding(false));

        await RequireGitAsync(RepositoryPath, "config", "--local", GitConfigKeys.HooksPath, ToGitPath(hooks));
    }

    public bool MarkerExists => File.Exists(MarkerPath);

    public void ClearMarker()
    {
        if (File.Exists(MarkerPath))
        {
            File.Delete(MarkerPath);
        }
    }

    public WorkspaceRequest Request(string taskKey = "HUB-001", int attempt = 1, string defaultBranch = "main") =>
        new()
        {
            RepositoryPath = RepositoryPath,
            RepositoryTrustRoot = Root,
            WorkspaceRoot = WorkspaceRoot,
            OrganizationSlug = "hub-dev",
            RepositorySlug = "hub-sample",
            TaskKey = taskKey,
            AttemptNo = attempt,
            DefaultBranch = defaultBranch,
        };

    public static string ToGitPath(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp file must never fail a test run.
        }
    }

    private static string ResolveGit()
    {
        Assert.True(GitToolchain.TryResolve(out var path, out var diagnostic), diagnostic);
        return path!;
    }
}
