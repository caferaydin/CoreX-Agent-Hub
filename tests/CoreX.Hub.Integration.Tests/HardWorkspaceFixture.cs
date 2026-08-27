using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardWorkspace;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A Windows source repository and a disposable Linux environment root, for one test class.
///
/// What it builds on the host is an ordinary git repository containing exactly what the transfer
/// tests need to be meaningful: a committed file, an ignored file holding a synthetic secret, an
/// untracked file, and a dirty modification to the committed one. If any of those three reaches
/// the other side of the boundary, the transfer is not what it claims to be.
///
/// What it builds inside the environment is one uniquely named directory under <c>/var/tmp</c>,
/// removed when the tests finish. It does not touch <c>/etc/wsl.conf</c>, <c>/etc/sudoers</c>,
/// any user, any group or any provider home, and it registers nothing.
///
/// On the service identity, and this is the honest part: the machine has no dedicated Hub service
/// account, so the fixture configures the profile's service user as <c>root</c>. That models the
/// ownership split - the controlled repository belongs to somebody the agent is not - without
/// creating a persistent account on the operator's distribution. The production contract still
/// requires a configured <c>ServiceUser</c>, and nothing in the implementation special-cases root.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardWorkspaceFixture : IAsyncLifetime
{
    /// <summary>The untrusted identity the worktree is handed to.</summary>
    public const string AgentUser = "daemon";

    /// <summary>See the note above: root stands in for a dedicated service account.</summary>
    public const string ServiceUser = "root";

    public const string SecretMarker = "COREX-IGNORED-SECRET-MUST-NOT-TRANSFER";
    public const string UntrackedMarker = "COREX-UNTRACKED-MUST-NOT-TRANSFER";
    public const string DirtyMarker = "COREX-DIRTY-MUST-NOT-TRANSFER";
    public const string CommittedContent = "committed content";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private string _hostRoot = string.Empty;

    public string EnvironmentId { get; private set; } = string.Empty;

    /// <summary>The Windows repository the pinned commit comes from.</summary>
    public string SourceRepository { get; private set; } = string.Empty;

    /// <summary>The commit the preflight will pin.</summary>
    public string BaseCommitSha { get; private set; } = string.Empty;

    /// <summary>The unique Linux directory this fixture owns.</summary>
    public string ExecutionRoot { get; } =
        "/var/tmp/corex-hard-workspace-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    public Guid RepositoryId { get; } = Guid.CreateVersion7();

    public bool Available => EnvironmentId.Length > 0 && SourceRepository.Length > 0;

    public WorkspaceManager Windows { get; private set; } = null!;

    public HardWorkspaceLayout Layout => HardWorkspaceLayout.For(ExecutionRoot);

    public async Task InitializeAsync()
    {
        _hostRoot = Path.Combine(
            Path.GetTempPath(), "corex-hard-workspace", Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(_hostRoot);

        EnvironmentId = await FindEnvironmentAsync();

        if (!GitToolchain.TryResolve(out var git, out _) || git is null)
        {
            return;
        }

        Windows = new WorkspaceManager(
            new ProcessGitCommandExecutor(git, [_hostRoot]),
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = Path.Combine(_hostRoot, "control") });

        await BuildSourceRepositoryAsync(git);
    }

    public async Task DisposeAsync()
    {
        if (EnvironmentId.Length > 0)
        {
            await ControlAsync(["/usr/bin/rm", "-rf", ExecutionRoot]);
        }

        try
        {
            if (Directory.Exists(_hostRoot))
            {
                // Git leaves read-only object files behind; they still belong to this test.
                foreach (var file in Directory.EnumerateFiles(_hostRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_hostRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public HardIsolationProfile Profile()
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = EnvironmentId,
            AgentUser = AgentUser,
            ServiceUser = ServiceUser,
            ExecutionRoot = ExecutionRoot,
            EvidenceRoot = ExecutionRoot + "-evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 512L * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    public RepositoryLocation Location() => new(
        SourceRepository,
        _hostRoot,
        Path.Combine(_hostRoot, "workspaces"),
        "hard-fixture",
        "hard-repo",
        "main");

    public HardWorkspaceGateway Gateway()
    {
        if (!GitToolchain.TryResolve(out var git, out _) || git is null)
        {
            throw new InvalidOperationException("git is not available on this machine.");
        }

        return HardWorkspaceGateway.Create(
            Windows,
            _ => Location(),
            new ProcessGitCommandExecutor(git, [_hostRoot]),
            Profile(),
            _hostRoot,
            _hostRoot);
    }

    /// <summary>Runs a command in the environment as root, the way the tests inspect it.</summary>
    public async Task<(int? ExitCode, string Stdout, string Stderr)> ControlAsync(
        IReadOnlyList<string> arguments,
        string user = "root",
        string? stdin = null)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return (null, string.Empty, string.Empty);
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(_hostRoot)).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", EnvironmentId, "-u", user, "--exec", .. arguments],
                WorkingDirectory = _hostRoot,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                StdinPayload = stdin,
                HardTimeout = TimeSpan.FromMinutes(3),
                MaxCapturedCharactersPerStream = 8 * 1024 * 1024,
            },
            CancellationToken.None);

        return (result.ExitCode, result.NormalizedStdout, result.NormalizedStderr);
    }

    /// <summary>The host repository's own git, for asserting the source was left alone.</summary>
    public async Task<string> SourceGitAsync(params string[] arguments)
    {
        if (!GitToolchain.TryResolve(out var git, out _) || git is null)
        {
            return string.Empty;
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(_hostRoot)).RunAsync(
            new ProcessRequest
            {
                Executable = git,
                Arguments = arguments,
                WorkingDirectory = SourceRepository,
                EnvironmentAllowlist = Workspace.Git.GitEnvironment.InheritedAllowlist,
                HardTimeout = TimeSpan.FromMinutes(2),
            },
            CancellationToken.None);

        return result.NormalizedStdout.Trim();
    }

    /// <summary>
    /// A repository whose working tree carries three things the transfer must not move.
    ///
    /// The committed content is deliberately different from the dirty content, so "the worktree
    /// contains the committed version" is a statement a test can check rather than a hope.
    /// </summary>
    private async Task BuildSourceRepositoryAsync(string git)
    {
        SourceRepository = Path.Combine(_hostRoot, "source");
        Directory.CreateDirectory(SourceRepository);

        await SourceGitAsync("init", "--initial-branch=main", ".");
        await SourceGitAsync("config", "user.name", "Fixture");
        await SourceGitAsync("config", "user.email", "fixture@corex.local");

        await File.WriteAllTextAsync(
            Path.Combine(SourceRepository, "tracked.txt"), CommittedContent + "\n", Utf8);

        await File.WriteAllTextAsync(Path.Combine(SourceRepository, ".gitignore"), "secret.txt\n", Utf8);

        await SourceGitAsync("add", "-A");
        await SourceGitAsync("commit", "-m", "base");

        BaseCommitSha = await SourceGitAsync("rev-parse", "main");

        // Everything below this line exists only in the working tree, and must stay there.
        await File.WriteAllTextAsync(Path.Combine(SourceRepository, "secret.txt"), SecretMarker + "\n", Utf8);
        await File.WriteAllTextAsync(Path.Combine(SourceRepository, "untracked.txt"), UntrackedMarker + "\n", Utf8);
        await File.WriteAllTextAsync(
            Path.Combine(SourceRepository, "tracked.txt"), CommittedContent + "\n" + DirtyMarker + "\n", Utf8);
    }

    private async Task<string> FindEnvironmentAsync()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return string.Empty;
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(_hostRoot)).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["--list", "--quiet"],
                WorkingDirectory = _hostRoot,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromSeconds(90),
            },
            CancellationToken.None);

        if (result.ExitCode != 0)
        {
            return string.Empty;
        }

        var names = result.NormalizedStdout
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return names.FirstOrDefault(n =>
                   n.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)
                   || n.Contains("debian", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault()
               ?? string.Empty;
    }
}
