using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using CoreX.Hub.Workspace.Abstractions;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Workspace;

/// <summary>
/// Git and worktree isolation (architecture G).
///
/// Every managed git invocation goes through the injected executor, which the composition
/// layer backs with ProcessRunner, so there is exactly one operating system process launch
/// point in the system (E.1 #1, #4). Every managed path is validated through the injected
/// path guard rather than by this module (E.1 #2).
///
/// This type is also the only production location allowed to invoke git commit
/// (E.1 #7). Whether a commit is permitted at all - the K0..K3 gates - is Phase 5 and is
/// deliberately absent here; Phase 3 supplies the primitive only.
///
/// There is no push, merge, pull, fetch, publish or deploy path, and no history repair
/// path: no reset, no rebase, no amend, no forced branch update (E.1 #9).
/// </summary>
public sealed class WorkspaceManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IGitCommandExecutor _git;
    private readonly IWorkspacePathGuard _paths;
    private readonly WorkspaceOptions _options;
    private readonly HubHooksLayout _hooks;

    public WorkspaceManager(IGitCommandExecutor git, IWorkspacePathGuard paths, WorkspaceOptions options)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hooks = HubHooks.Materialise(options.HubControlRoot);
    }

    public HubHooksLayout Hooks => _hooks;

    /// <summary>
    /// Read-only repository inspection. Nothing is fetched, pulled, repaired or written
    /// (Phase 3 brief section 8).
    /// </summary>
    public async Task<RepositoryPreflightResult> PreflightAsync(
        string repositoryPath,
        string repositoryTrustRoot,
        string defaultBranch,
        CancellationToken cancellationToken)
    {
        var issues = new List<RepositoryIssue>();

        var decision = _paths.Validate(repositoryTrustRoot, repositoryPath, mustExist: true);
        if (!decision.IsAllowed)
        {
            issues.Add(new RepositoryIssue(RepositoryIssueCode.PathRejected, decision.Reason ?? "path refused"));
            return new RepositoryPreflightResult { IsUsable = false, Issues = issues };
        }

        var repository = decision.CanonicalPath!;

        var insideWorkTree = await GitAsync(repository, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!insideWorkTree.Succeeded)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.NotAGitRepository,
                $"'{repository}' is not a git working tree ({insideWorkTree.Describe()})"));
            return new RepositoryPreflightResult
            {
                IsUsable = false,
                Issues = issues,
                CanonicalRepositoryPath = repository,
            };
        }

        var isBare = await ReadValueAsync(repository, ["rev-parse", "--is-bare-repository"], cancellationToken);
        if (string.Equals(isBare, "true", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new RepositoryIssue(RepositoryIssueCode.BareRepository, "bare repositories are not supported"));
        }

        // A linked worktree reports a .git file rather than a directory; using one as a
        // source repository would nest managed worktrees.
        // --path-format=absolute is required: the common dir is otherwise reported
        // relative to the working directory, which would make every repository look linked.
        var commonDir = await ReadValueAsync(
            repository, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken);
        var gitDir = await ReadValueAsync(
            repository, ["rev-parse", "--path-format=absolute", "--git-dir"], cancellationToken);
        if (!string.IsNullOrEmpty(commonDir)
            && !string.IsNullOrEmpty(gitDir)
            && !PathsLookEqual(commonDir, gitDir))
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.IsAnotherWorktree,
                $"'{repository}' is a linked worktree of '{commonDir}'"));
        }

        var hasSubmodules = await HasSubmodulesAsync(repository, cancellationToken);
        if (hasSubmodules)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.SubmodulesUnsupported,
                "repositories with submodules are rejected in the MVP"));
        }

        var usesLfs = await UsesGitLfsAsync(repository, cancellationToken);
        if (usesLfs)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.GitLfsUnsupported,
                "Git LFS repositories are rejected in the MVP"));
        }

        await EnsureWorktreeConfigCompatibleAsync(repository, issues, cancellationToken);

        var branchExists = await GitAsync(
            repository,
            ["rev-parse", "--verify", "--quiet", "refs/heads/" + defaultBranch],
            cancellationToken);

        string? baseCommit = null;
        if (!branchExists.Succeeded)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.DefaultBranchMissing,
                $"branch '{defaultBranch}' does not exist"));
        }
        else
        {
            baseCommit = await ReadValueAsync(
                repository,
                ["rev-parse", "--verify", defaultBranch + "^{commit}"],
                cancellationToken);

            if (string.IsNullOrWhiteSpace(baseCommit) || baseCommit.Length != 40)
            {
                issues.Add(new RepositoryIssue(
                    RepositoryIssueCode.DefaultBranchUnresolvable,
                    $"branch '{defaultBranch}' does not resolve to a commit"));
                baseCommit = null;
            }
        }

        return new RepositoryPreflightResult
        {
            IsUsable = issues.Count == 0,
            Issues = issues,
            CanonicalRepositoryPath = repository,
            DefaultBranch = defaultBranch,
            BaseCommitSha = baseCommit,
            HasSubmodules = hasSubmodules,
            UsesGitLfs = usesLfs,
            IsBare = string.Equals(isBare, "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Creates the pinned, hardened agent worktree.
    ///
    /// The first checkout capable command already runs under Hub controlled hooks
    /// configuration, because git worktree add itself invokes post-checkout and a
    /// repository is free to point core.hooksPath at its own directory.
    /// </summary>
    public async Task<WorkspacePreparationResult> PrepareAsync(
        WorkspaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var preflight = await PreflightAsync(
            request.RepositoryPath,
            request.RepositoryTrustRoot,
            request.DefaultBranch,
            cancellationToken);

        if (!preflight.IsUsable || preflight.BaseCommitSha is null)
        {
            return new WorkspacePreparationResult
            {
                Succeeded = false,
                Preflight = preflight,
                Issues = [new WorkspaceIssue(WorkspaceIssueCode.RepositoryUnusable, preflight.Describe())],
            };
        }

        var repository = preflight.CanonicalRepositoryPath!;

        var workspaceDecision = _paths.Validate(
            request.WorkspaceRoot,
            Path.Combine(request.WorkspaceRoot, request.RelativeWorkspacePath),
            mustExist: false);

        if (!workspaceDecision.IsAllowed)
        {
            return Failed(preflight, WorkspaceIssueCode.WorkspacePathRejected, workspaceDecision.Reason ?? "refused");
        }

        var workspacePath = workspaceDecision.CanonicalPath!;

        if (Directory.Exists(workspacePath) || File.Exists(workspacePath))
        {
            return Failed(preflight, WorkspaceIssueCode.WorkspacePathAlreadyExists, workspacePath);
        }

        var branchExists = await GitAsync(
            repository,
            ["rev-parse", "--verify", "--quiet", "refs/heads/" + request.BranchName],
            cancellationToken);

        if (branchExists.Succeeded)
        {
            return Failed(preflight, WorkspaceIssueCode.BranchAlreadyExists, request.BranchName);
        }

        var gate = RepositoryLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);

            // The worktree is created from the pinned commit, not from the branch, so a
            // dirty source working tree and a moving branch are both irrelevant.
            var add = await GitAsync(
                repository,
                ["worktree", "add", "--no-track", "-b", request.BranchName, workspacePath, preflight.BaseCommitSha],
                cancellationToken);

            if (!add.Succeeded)
            {
                return Failed(preflight, WorkspaceIssueCode.WorktreeAddFailed, add.Describe());
            }
        }
        finally
        {
            gate.Release();
        }

        var hardening = await HardenAsync(repository, workspacePath, cancellationToken);
        if (hardening is not null)
        {
            return Failed(preflight, WorkspaceIssueCode.HardeningFailed, hardening);
        }

        return new WorkspacePreparationResult
        {
            Succeeded = true,
            Preflight = preflight,
            Workspace = new PreparedWorkspace
            {
                RepositoryPath = repository,
                WorkspacePath = workspacePath,
                BranchName = request.BranchName,
                BaseCommitSha = preflight.BaseCommitSha,
                HooksDirectory = _hooks.HooksDirectory,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
        };
    }

    /// <summary>
    /// Persists worktree scoped hardening so the protections stay effective for later
    /// commands that do not carry the Hub environment - including commands an agent runs
    /// itself. Worktree scoped configuration is used so the developer's main worktree
    /// keeps its own behaviour.
    /// </summary>
    private async Task<string?> HardenAsync(string repository, string workspacePath, CancellationToken cancellationToken)
    {
        var enableExtension = await GitAsync(
            repository,
            ["config", "--local", GitConfigKeys.WorktreeConfigExtension, "true"],
            cancellationToken);

        if (!enableExtension.Succeeded)
        {
            return $"could not enable worktree scoped configuration ({enableExtension.Describe()})";
        }

        (string Key, string Value)[] settings =
        [
            (GitConfigKeys.HooksPath, _hooks.HooksDirectory),
            (GitConfigKeys.PushUrl, GitConfigKeys.DisabledPushUrl),
            (GitConfigKeys.CredentialHelper, string.Empty),
            (GitConfigKeys.LongPaths, "true"),
            (GitConfigKeys.GcAuto, "0"),
        ];

        foreach (var setting in settings)
        {
            var result = await GitAsync(
                workspacePath,
                ["config", "--worktree", setting.Key, setting.Value],
                cancellationToken);

            if (!result.Succeeded)
            {
                return $"could not set {setting.Key} ({result.Describe()})";
            }
        }

        return null;
    }

    /// <summary>
    /// Collects git truth about the workspace. No outcome is computed and no gate is run:
    /// scope and secret gates belong to Phase 5.
    /// </summary>
    public async Task<CaptureChangesResult> CaptureChangesAsync(
        PreparedWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var head = await ReadValueAsync(workspace.WorkspacePath, ["rev-parse", "HEAD"], cancellationToken);

        var status = await GitAsync(
            workspace.WorkspacePath,
            ["status", "--porcelain=v2", "--untracked-files=all", "--branch"],
            cancellationToken);

        var graph = await GitAsync(
            workspace.WorkspacePath,
            ["log", "--format=%H%x09%an%x09%aI%x09%s", workspace.BaseCommitSha + "..HEAD"],
            cancellationToken);

        var unexpected = await GitAsync(
            workspace.WorkspacePath,
            ["rev-list", workspace.BaseCommitSha + "..HEAD"],
            cancellationToken);

        // Intent-to-add makes new untracked files visible to git diff without staging
        // their content and without creating a commit. Ignored files are still skipped,
        // so a gitignored secret never reaches the evidence.
        await GitAsync(workspace.WorkspacePath, ["add", "-N", "."], cancellationToken);

        var diff = await GitAsync(
            workspace.WorkspacePath,
            ["diff", workspace.BaseCommitSha],
            cancellationToken);

        var nameStatus = await GitAsync(
            workspace.WorkspacePath,
            ["diff", "--name-status", workspace.BaseCommitSha],
            cancellationToken);

        var changed = ParseNameStatus(nameStatus.Stdout);

        return new CaptureChangesResult
        {
            BaseCommitSha = workspace.BaseCommitSha,
            CurrentHeadSha = head ?? string.Empty,
            UnexpectedCommitShas = SplitLines(unexpected.Stdout),
            GitStatus = status.Stdout,
            GitCommitGraph = graph.Stdout,
            GitWorktreeDiff = diff.Stdout,
            ChangedPaths = [.. changed.Select(c => c.Path)],
            FileInventory = BuildInventory(workspace.WorkspacePath, changed),
        };
    }

    /// <summary>
    /// The only production location allowed to invoke git commit (E.1 #7).
    /// It never manufactures an empty commit and never uses a bypass flag.
    /// </summary>
    public async Task<ResultCommitOutcome> CreateResultCommitAsync(
        PreparedWorkspace workspace,
        string taskKey,
        int attemptNo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var stage = await GitAsync(workspace.WorkspacePath, ["add", "-A"], cancellationToken);
        if (!stage.Succeeded)
        {
            return new ResultCommitOutcome { State = ResultCommitState.Failed, Detail = stage.Describe() };
        }

        var staged = await GitAsync(
            workspace.WorkspacePath,
            ["diff", "--cached", "--name-only"],
            cancellationToken);

        if (!staged.Succeeded)
        {
            return new ResultCommitOutcome { State = ResultCommitState.Failed, Detail = staged.Describe() };
        }

        if (string.IsNullOrWhiteSpace(staged.Stdout))
        {
            return new ResultCommitOutcome
            {
                State = ResultCommitState.NoChanges,
                Detail = "nothing to commit; an empty commit is never created",
            };
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"agent: {taskKey} attempt {attemptNo}");

        var commit = await GitAsync(
            workspace.WorkspacePath,
            ["commit", "-m", message],
            cancellationToken,
            GitEnvironment.WithHubIdentity(BuildEnvironment()));

        if (!commit.Succeeded)
        {
            return new ResultCommitOutcome { State = ResultCommitState.Failed, Detail = commit.Describe() };
        }

        var sha = await ReadValueAsync(workspace.WorkspacePath, ["rev-parse", "HEAD"], cancellationToken);

        var diff = await GitAsync(
            workspace.WorkspacePath,
            ["diff", workspace.BaseCommitSha + ".." + (sha ?? "HEAD")],
            cancellationToken);

        return new ResultCommitOutcome
        {
            State = ResultCommitState.Created,
            CommitSha = sha,
            CommitDiff = diff.Stdout,
        };
    }

    /// <summary>
    /// ADR-0016: a workspace without a result commit holds the only copy of the work, so
    /// automatic release is denied and an explicit operator confirmation is required.
    /// Removal is attempted through git, never by deleting the directory, and a stuck
    /// worktree becomes visible as orphaned rather than being erased.
    /// </summary>
    public async Task<WorkspaceReleaseResult> ReleaseAsync(
        PreparedWorkspace workspace,
        bool hasResultCommit,
        bool operatorConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!hasResultCommit && !operatorConfirmed)
        {
            return new WorkspaceReleaseResult
            {
                State = WorkspaceReleaseState.DeniedNoResultCommit,
                Detail =
                    "the workspace has no result commit, so it holds the only copy of the work; "
                    + "release requires explicit operator confirmation",
            };
        }

        var gate = RepositoryLocks.GetOrAdd(workspace.RepositoryPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            string lastDetail = string.Empty;

            for (var attempt = 1; attempt <= _options.WorktreeRemovalAttempts; attempt++)
            {
                var remove = await GitAsync(
                    workspace.RepositoryPath,
                    ["worktree", "remove", workspace.WorkspacePath],
                    cancellationToken);

                if (remove.Succeeded)
                {
                    return new WorkspaceReleaseResult
                    {
                        State = WorkspaceReleaseState.Released,
                        RemovalAttempts = attempt,
                    };
                }

                lastDetail = remove.Describe();

                if (attempt < _options.WorktreeRemovalAttempts)
                {
                    await Task.Delay(_options.RemovalRetryDelay, cancellationToken);
                }
            }

            return new WorkspaceReleaseResult
            {
                State = WorkspaceReleaseState.Orphaned,
                RemovalAttempts = _options.WorktreeRemovalAttempts,
                Detail = lastDetail,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reports workspace truth by comparing what the Hub knows, what git lists and what is
    /// actually on disk. It never deletes anything, and it never invents an execution
    /// state transition: that is Phase 5.
    /// </summary>
    public async Task<ReconcileReport> ReconcileAsync(
        string repositoryPath,
        IReadOnlyList<KnownWorkspace> known,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(known);

        var list = await GitAsync(repositoryPath, ["worktree", "list", "--porcelain"], cancellationToken);
        var listed = ParseWorktreeList(list.Stdout, repositoryPath);

        var entries = new List<WorkspaceStatusEntry>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in known)
        {
            var match = listed.FirstOrDefault(l => PathsLookEqual(l.Path, workspace.WorktreePath));

            if (match is null)
            {
                entries.Add(new WorkspaceStatusEntry(
                    workspace.WorktreePath,
                    workspace.BranchName,
                    null,
                    WorkspaceHealth.Missing,
                    "known to the Hub but not listed by git"));
                continue;
            }

            matched.Add(match.Path);

            var health = Directory.Exists(match.Path) ? WorkspaceHealth.Healthy : WorkspaceHealth.RemovalFailed;
            var detail = health == WorkspaceHealth.Healthy
                ? "registered and present"
                : "listed by git but the directory is gone";

            entries.Add(new WorkspaceStatusEntry(match.Path, match.Branch, match.Head, health, detail));
        }

        foreach (var orphan in listed.Where(l => !matched.Contains(l.Path) && !l.IsMainWorktree))
        {
            entries.Add(new WorkspaceStatusEntry(
                orphan.Path,
                orphan.Branch,
                orphan.Head,
                WorkspaceHealth.Orphaned,
                "listed by git but unknown to the Hub; reported, never removed automatically"));
        }

        return new ReconcileReport(entries);
    }

    private Dictionary<string, string> BuildEnvironment() =>
        GitEnvironment.WithHardenedConfig(_hooks.EmptyGlobalConfigPath, _hooks.HooksDirectory);

    private Task<GitCommandResult> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Dictionary<string, string>? environment = null) =>
        _git.ExecuteAsync(
            new GitCommand
            {
                WorkingDirectory = workingDirectory,
                Arguments = arguments,
                Environment = environment ?? BuildEnvironment(),
                Timeout = _options.GitTimeout,
            },
            cancellationToken);

    private async Task<string?> ReadValueAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await GitAsync(workingDirectory, arguments, cancellationToken);
        return result.Succeeded ? result.Stdout.Trim() : null;
    }

    private async Task<bool> HasSubmodulesAsync(string repository, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(repository, ".gitmodules")))
        {
            return true;
        }

        var listed = await GitAsync(repository, ["ls-files", "--stage"], cancellationToken);

        // Gitlink entries are recorded with mode 160000.
        return listed.Succeeded
            && listed.Stdout.Split('\n').Any(l => l.StartsWith("160000 ", StringComparison.Ordinal));
    }

    private async Task<bool> UsesGitLfsAsync(string repository, CancellationToken cancellationToken)
    {
        var attributesFile = Path.Combine(repository, ".gitattributes");
        if (File.Exists(attributesFile)
            && File.ReadAllText(attributesFile).Contains("filter=lfs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tracked = await GitAsync(repository, ["ls-files", "--", ".gitattributes"], cancellationToken);
        if (!tracked.Succeeded || string.IsNullOrWhiteSpace(tracked.Stdout))
        {
            return false;
        }

        var content = await GitAsync(repository, ["show", "HEAD:.gitattributes"], cancellationToken);
        return content.Succeeded && content.Stdout.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Worktree scoped configuration changes how core.bare and core.worktree are read, so
    /// a repository that relies on either is rejected rather than silently rewritten
    /// (Phase 3 brief section 13).
    /// </summary>
    private async Task EnsureWorktreeConfigCompatibleAsync(
        string repository,
        List<RepositoryIssue> issues,
        CancellationToken cancellationToken)
    {
        var coreWorktree = await GitAsync(
            repository,
            ["config", "--local", "--get", GitConfigKeys.CoreWorktree],
            cancellationToken);

        if (coreWorktree.Succeeded && !string.IsNullOrWhiteSpace(coreWorktree.Stdout))
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.IncompatibleWorktreeConfiguration,
                $"{GitConfigKeys.CoreWorktree} is set; the Hub will not rewrite it"));
        }

        var coreBare = await GitAsync(
            repository,
            ["config", "--local", "--get", GitConfigKeys.CoreBare],
            cancellationToken);

        if (coreBare.Succeeded && string.Equals(coreBare.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.IncompatibleWorktreeConfiguration,
                $"{GitConfigKeys.CoreBare} is true in local configuration"));
        }
    }

    private static WorkspacePreparationResult Failed(
        RepositoryPreflightResult preflight,
        WorkspaceIssueCode code,
        string detail) =>
        new()
        {
            Succeeded = false,
            Preflight = preflight,
            Issues = [new WorkspaceIssue(code, detail)],
        };

    private static IReadOnlyList<string> SplitLines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)];

    private static List<(string Status, string Path)> ParseNameStatus(string text)
    {
        var entries = new List<(string, string)>();

        foreach (var line in SplitLines(text))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                entries.Add((parts[0], parts[^1]));
            }
        }

        return entries;
    }

    private IReadOnlyList<WorkspaceFileEntry> BuildInventory(
        string workspacePath,
        IReadOnlyList<(string Status, string Path)> changed)
    {
        var inventory = new List<WorkspaceFileEntry>(changed.Count);

        foreach (var entry in changed.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // Every inventoried path passes the trust boundary, so an escaping reparse
            // point can never be followed and nothing outside the workspace is hashed.
            var decision = _paths.Validate(workspacePath, Path.Combine(workspacePath, entry.Path), mustExist: false);

            if (!decision.IsAllowed || !File.Exists(decision.CanonicalPath))
            {
                inventory.Add(new WorkspaceFileEntry(entry.Path, entry.Status, 0, null, false));
                continue;
            }

            var absolute = decision.CanonicalPath!;
            var info = new FileInfo(absolute);
            string? hash = null;

            if (info.Length <= _options.MaxHashedFileBytes)
            {
                using var stream = File.OpenRead(absolute);
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            inventory.Add(new WorkspaceFileEntry(entry.Path, entry.Status, info.Length, hash, true));
        }

        return inventory;
    }

    private static List<WorktreeListEntry> ParseWorktreeList(string text, string repositoryPath)
    {
        var entries = new List<WorktreeListEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;

        void Flush()
        {
            if (path is not null)
            {
                entries.Add(new WorktreeListEntry(path, head, branch, PathsLookEqual(path, repositoryPath)));
            }

            path = null;
            head = null;
            branch = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line["branch ".Length..];
            }
        }

        Flush();
        return entries;
    }

    private static bool PathsLookEqual(string left, string right) =>
        string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    private sealed record WorktreeListEntry(string Path, string? Head, string? Branch, bool IsMainWorktree);
}
