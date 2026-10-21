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
public sealed partial class WorkspaceManager
{
    /// <summary>Never let a repository choose the program that renders evidence.</summary>
    private const string NoExternalDiff = "--no-ext-diff";

    private const string NoTextConv = "--no-textconv";

    /// <summary>
    /// The two git configuration scopes a managed worktree actually answers from.
    ///
    /// Shared configuration is inherited by every worktree of the repository. Worktree
    /// scoped configuration belongs to one worktree, is read after the shared file so it
    /// wins, and - unlike everything the Hub supplies per command - is writable from
    /// inside the worktree itself. Both are inspected; inspecting only the first left the
    /// mutable one unguarded.
    /// </summary>
    private const string SharedConfigurationScope = "--local";

    private const string WorktreeConfigurationScope = "--worktree";

    /// <summary>Git's own name for the file; the working tree scan looks for nothing else.</summary>
    private const string AttributesFileName = ".gitattributes";

    /// <summary>
    /// The one filter driver that is reported as its own kind of unsupported rather than as
    /// an unknown program, because "this repository uses LFS" is a different conversation
    /// from "this repository names a program the Hub will not run".
    /// </summary>
    private const string LfsFilterDriver = "lfs";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lock identity for one physical repository.
    ///
    /// Two calls that address the same repository must never take two different locks, so
    /// the key is normalised the way Windows compares paths: a single separator form and
    /// no trailing separator. Case is handled by the dictionary comparer. The path handed
    /// in has already passed the managed path boundary, so this only removes the spelling
    /// differences that boundary is allowed to preserve.
    /// </summary>
    public static string RepositoryLockKey(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var normalised = repositoryPath
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar);

        // A root such as "D:\" keeps its separator; anything else loses the trailing one.
        return normalised.Length > 3
            ? normalised.TrimEnd(Path.DirectorySeparatorChar)
            : normalised;
    }

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

        await EnsureWorktreeConfigCompatibleAsync(repository, issues, cancellationToken);

        var unsafeConfiguration = await InspectRepositoryConfigurationAsync(repository, cancellationToken);
        issues.AddRange(unsafeConfiguration);

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

        var usesLfs = false;
        if (baseCommit is not null)
        {
            var attributes = await InspectTrackedAttributesAsync(repository, baseCommit, cancellationToken);
            usesLfs = attributes.UsesLfs;
            issues.AddRange(attributes.Issues);
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
    /// Inspects the repository and then creates the worktree from whatever that inspection
    /// pinned. Convenience for callers that have no reason to hold the two apart.
    ///
    /// A caller that has to record the pinned commit somewhere before the worktree exists -
    /// an execution's provenance, for instance - must not use this: it would pin twice, and
    /// the second answer is the one that reaches the worktree. Preflight once and hand the
    /// result to the overload below.
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

        return await PrepareAsync(request, preflight, cancellationToken);
    }

    /// <summary>
    /// Creates the pinned, hardened agent worktree from a commit a preflight already
    /// resolved.
    ///
    /// The default branch is deliberately not resolved again. A caller that pinned a
    /// commit and acted on it - recorded it, keyed something on it - has to get that exact
    /// commit here, so a branch that advanced in between is simply not the authority any
    /// more. The captured commit is.
    ///
    /// The preflight has to be one this module produced, for this repository, so there is
    /// no way in to a commit the repository never answered with: what is handed back is
    /// checked against the repository the request names before anything is created.
    ///
    /// The first checkout capable command already runs under Hub controlled hooks
    /// configuration, because git worktree add itself invokes post-checkout and a
    /// repository is free to point core.hooksPath at its own directory.
    /// </summary>
    public async Task<WorkspacePreparationResult> PrepareAsync(
        WorkspaceRequest request,
        RepositoryPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preflight);

        if (!preflight.IsUsable || preflight.BaseCommitSha is null || preflight.CanonicalRepositoryPath is null)
        {
            return new WorkspacePreparationResult
            {
                Succeeded = false,
                Preflight = preflight,
                Issues = [new WorkspaceIssue(WorkspaceIssueCode.RepositoryUnusable, preflight.Describe())],
            };
        }

        if (!string.Equals(preflight.DefaultBranch, request.DefaultBranch, StringComparison.Ordinal))
        {
            return Failed(
                preflight,
                WorkspaceIssueCode.RepositoryUnusable,
                $"the preflight pinned '{preflight.DefaultBranch}' but '{request.DefaultBranch}' was requested");
        }

        var repositoryDecision = _paths.Validate(request.RepositoryTrustRoot, request.RepositoryPath, mustExist: true);

        if (!repositoryDecision.IsAllowed)
        {
            return Failed(
                preflight, WorkspaceIssueCode.RepositoryUnusable, repositoryDecision.Reason ?? "path refused");
        }

        if (!PathsLookEqual(repositoryDecision.CanonicalPath!, preflight.CanonicalRepositoryPath))
        {
            return Failed(
                preflight,
                WorkspaceIssueCode.RepositoryUnusable,
                $"the preflight belongs to '{preflight.CanonicalRepositoryPath}', not to '{request.RepositoryPath}'");
        }

        var repository = preflight.CanonicalRepositoryPath;

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

        // Everything that mutates shared repository metadata runs inside one repository
        // scoped critical section. Hardening belongs here as well, not only the worktree
        // add: enabling extensions.worktreeConfig rewrites <repo>/.git/config, and every
        // subsequent --worktree write has to read that same file. Leaving hardening
        // outside the section let one preparation rewrite .git/config while another was
        // reading it, which git reports on Windows as "unable to access .git/config:
        // Permission denied".
        var gate = RepositoryLocks.GetOrAdd(RepositoryLockKey(repository), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Safety is re-read here, inside the lock and before anything exists, because
            // the preflight may be old: a caller that pins a commit, records it and only
            // then asks for a worktree leaves a window, and repository-local configuration
            // is shared mutable state that anything can change inside it. Checking out is
            // itself the dangerous operation - git worktree add runs filters and hooks - so
            // noticing afterwards and deleting the tree would mean the unsafe program had
            // already run.
            //
            // This is the same authority that guards staging and evidence collection, not a
            // second policy: one place decides what unsafe configuration means.
            if (await RevalidateSafetyAsync(repository, preflight.BaseCommitSha, cancellationToken)
                is { } unsafeConfiguration)
            {
                return Failed(preflight, WorkspaceIssueCode.RepositoryUnusable, unsafeConfiguration);
            }

            // Provenance is untouched by that: the pinned commit is verified as itself, and
            // the default branch is never consulted again. A branch that moved is allowed;
            // configuration that turned unsafe is not.
            var pinned = await GitAsync(
                repository,
                ["rev-parse", "--verify", "--quiet", preflight.BaseCommitSha + "^{commit}"],
                cancellationToken);

            if (!pinned.Succeeded)
            {
                return Failed(
                    preflight,
                    WorkspaceIssueCode.RepositoryUnusable,
                    $"the pinned commit {preflight.BaseCommitSha[..12]} is no longer resolvable "
                    + $"in '{repository}' ({pinned.Describe()})");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);

            // The worktree is created from the commit the preflight pinned, never from the
            // branch name, so a dirty source working tree and a branch that has since moved
            // are both irrelevant: this materialises the commit the caller already acted on.
            var add = await GitAsync(
                repository,
                ["worktree", "add", "--no-track", "-b", request.BranchName, workspacePath, preflight.BaseCommitSha],
                cancellationToken);

            if (!add.Succeeded)
            {
                return Failed(preflight, WorkspaceIssueCode.WorktreeAddFailed, add.Describe());
            }

            var hardening = await HardenAsync(repository, workspacePath, cancellationToken);
            if (hardening is not null)
            {
                return Failed(preflight, WorkspaceIssueCode.HardeningFailed, hardening);
            }
        }
        finally
        {
            gate.Release();
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

        // Configuration inspected when the workspace was created may since have changed,
        // and collecting evidence touches the working tree, so revalidate first. Reading
        // HEAD above is safe: it never consults the working tree.
        if (await RevalidateSafetyAsync(workspace, cancellationToken) is { } rejection)
        {
            return new CaptureChangesResult
            {
                BaseCommitSha = workspace.BaseCommitSha,
                CurrentHeadSha = head ?? string.Empty,
                SafetyRejection = rejection,
            };
        }

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

        // --no-ext-diff and --no-textconv are not optional: without them a repository
        // configured diff.external or diff.<driver>.textconv would run a program of the
        // repository's choosing while the Hub collects evidence.
        var diff = await GitAsync(
            workspace.WorkspacePath,
            ["diff", NoExternalDiff, NoTextConv, workspace.BaseCommitSha],
            cancellationToken);

        var nameStatus = await GitAsync(
            workspace.WorkspacePath,
            ["diff", NoExternalDiff, NoTextConv, "--name-status", workspace.BaseCommitSha],
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

        // git add can invoke a clean or process filter, so the repository is revalidated
        // before anything is staged rather than trusting an earlier inspection.
        if (await RevalidateSafetyAsync(workspace, cancellationToken) is { } rejection)
        {
            return new ResultCommitOutcome
            {
                State = ResultCommitState.RepositoryUnsafe,
                Detail = rejection,
            };
        }

        var stage = await GitAsync(workspace.WorkspacePath, ["add", "-A"], cancellationToken);
        if (!stage.Succeeded)
        {
            return new ResultCommitOutcome { State = ResultCommitState.Failed, Detail = stage.Describe() };
        }

        var staged = await GitAsync(
            workspace.WorkspacePath,
            ["diff", NoExternalDiff, NoTextConv, "--cached", "--name-only"],
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
            ["diff", NoExternalDiff, NoTextConv, workspace.BaseCommitSha + ".." + (sha ?? "HEAD")],
            cancellationToken);

        // What the tree still holds, in the same terms git itself uses to decide whether a
        // worktree may be removed: modified or untracked paths, ignored files excluded.
        // Everything was just staged, so this should be empty - and a caller about to
        // measure or release the tree needs to know when it is not.
        var remaining = await GitAsync(
            workspace.WorkspacePath,
            ["status", "--porcelain", "--untracked-files=normal"],
            cancellationToken);

        return new ResultCommitOutcome
        {
            State = ResultCommitState.Created,
            CommitSha = sha,
            CommitDiff = diff.Stdout,
            UncommittedPaths = remaining.Succeeded ? ParseStatusPaths(remaining.Stdout) : [],
            WorktreeStateKnown = remaining.Succeeded,
            Detail = remaining.Succeeded ? null : "the worktree state could not be read: " + remaining.Describe(),
        };
    }

    /// <summary>
    /// Paths out of <c>git status --porcelain</c>: two status characters, a space, then the
    /// path. Renames report "old -&gt; new"; the new name is the one that exists on disk.
    /// </summary>
    private static IReadOnlyList<string> ParseStatusPaths(string status)
    {
        var paths = new List<string>();

        foreach (var line in status.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.Length <= 3)
            {
                continue;
            }

            var path = trimmed[3..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);

            paths.Add(arrow >= 0 ? path[(arrow + 4)..] : path);
        }

        return paths;
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

        // No forcing here: git's refusal to remove a worktree holding uncommitted work is
        // the D6 protection, not an obstacle.
        return await RemoveWorktreeAsync(
            workspace.RepositoryPath,
            workspace.WorkspacePath,
            ["worktree", "remove", workspace.WorkspacePath],
            cancellationToken);
    }

    /// <summary>
    /// Removes one worktree under the repository critical section, with the bounded retry
    /// a briefly locked directory needs. Shared by execution worktree release and snapshot
    /// disposal so there is one removal path and one lock, not two.
    ///
    /// It never forces: a worktree that will not go away is reported as orphaned rather
    /// than having its git metadata deleted out from under it.
    /// </summary>
    private async Task<WorkspaceReleaseResult> RemoveWorktreeAsync(
        string repositoryPath,
        string worktreePath,
        IReadOnlyList<string> removeArguments,
        CancellationToken cancellationToken)
    {
        var gate = RepositoryLocks.GetOrAdd(RepositoryLockKey(repositoryPath), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            string lastDetail = string.Empty;

            for (var attempt = 1; attempt <= _options.WorktreeRemovalAttempts; attempt++)
            {
                var remove = await GitAsync(repositoryPath, removeArguments, cancellationToken);

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

    /// <summary>
    /// Whether <paramref name="branch"/> already contains <paramref name="commitSha"/>.
    ///
    /// Read-only (ADR-0028): the autopilot asks it so it never builds the next task on a base
    /// that lacks the last accepted result. Moving the branch is the operator's job, and
    /// nothing here does it. Null means the question could not be answered - an unknown
    /// commit, a refused path - which is never the same as "no".
    /// </summary>
    public async Task<bool?> BranchContainsAsync(
        string repositoryPath,
        string repositoryTrustRoot,
        string branch,
        string commitSha,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        var decision = _paths.Validate(repositoryTrustRoot, repositoryPath, mustExist: true);
        if (!decision.IsAllowed)
        {
            return null;
        }

        var ancestry = await GitAsync(
            decision.CanonicalPath!,
            ["merge-base", "--is-ancestor", commitSha, "refs/heads/" + branch],
            cancellationToken);

        // git's own contract: 0 is an ancestor, 1 is not, anything else is an error.
        return !ancestry.Started
            ? null
            : ancestry.ExitCode switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
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

    /// <summary>
    /// Shared repository configuration: <c>&lt;repo&gt;/.git/config</c>, which every
    /// worktree of that repository inherits.
    /// </summary>
    private Task<List<RepositoryIssue>> InspectRepositoryConfigurationAsync(
        string repository,
        CancellationToken cancellationToken) =>
        InspectConfigurationScopeAsync(
            repository, SharedConfigurationScope, "repository-local", cancellationToken);

    /// <summary>
    /// Worktree scoped configuration:
    /// <c>&lt;repo&gt;/.git/worktrees/&lt;name&gt;/config.worktree</c>.
    ///
    /// The scope the shared listing above cannot see, and the one an agent can write from
    /// inside its own worktree with a single ordinary git command. Leaving it uninspected
    /// meant a filter driver could be added there, an untracked .gitattributes could point
    /// at it, and the Hub's own git would run it while collecting evidence - measured, not
    /// hypothesised.
    /// </summary>
    private Task<List<RepositoryIssue>> InspectWorktreeConfigurationAsync(
        string workspacePath,
        CancellationToken cancellationToken) =>
        InspectConfigurationScopeAsync(
            workspacePath, WorktreeConfigurationScope, "worktree-scoped", cancellationToken);

    /// <summary>
    /// One configuration inspection, addressed by scope.
    ///
    /// Launching only git.exe is not by itself a secondary process boundary: git can be
    /// told to run external programs through configuration that is not a hook. Some of
    /// those surfaces are neutralised per command (core.fsmonitor, signing, diff helpers);
    /// the ones that cannot be neutralised reliably cause the repository to be rejected.
    ///
    /// Includes are read WITHOUT being followed, and are themselves rejected: validating
    /// the visible keys would be meaningless if ordinary git later imported a filter
    /// driver from an included file. Fail closed.
    ///
    /// Command strings are never interpreted. The presence of an unsupported key is the
    /// whole decision.
    ///
    /// Both scopes ask the same question of the same list. Two scopes and one authority is
    /// the point: a second family list would be a second thing to remember to update.
    /// </summary>
    private async Task<List<RepositoryIssue>> InspectConfigurationScopeAsync(
        string workingDirectory,
        string scope,
        string scopeLabel,
        CancellationToken cancellationToken)
    {
        var issues = new List<RepositoryIssue>();

        var listed = await GitAsync(
            workingDirectory,
            ["config", scope, "--list", "--no-includes", "--name-only", "-z"],
            cancellationToken);

        if (!listed.Succeeded)
        {
            // git exits 1 with nothing on stderr when the listing is simply empty, which is
            // not a finding. Anything else - an unreadable or momentarily locked config,
            // for instance - means the scan did not happen, and a scan that did not happen
            // must never be reported as "nothing unsafe found".
            var listingWasEmpty = listed.Started
                && listed.ExitCode == 1
                && string.IsNullOrWhiteSpace(listed.Stderr);

            if (!listingWasEmpty)
            {
                issues.Add(new RepositoryIssue(
                    RepositoryIssueCode.UnsafeGitConfiguration,
                    $"the {scopeLabel} configuration could not be read, so it could not be "
                    + $"checked for unsupported executable configuration ({listed.Describe()})"));
            }

            return issues;
        }

        foreach (var key in listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = key.Trim();

            foreach (var family in UnsupportedConfigurationFamilies)
            {
                if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new RepositoryIssue(
                        RepositoryIssueCode.UnsafeGitConfiguration,
                        $"{scopeLabel} '{name}' is not supported: {UnsupportedConfigurationReason(family)}"));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Asserts that the durable hardening <see cref="HardenAsync" /> wrote is still exactly
    /// what it wrote.
    ///
    /// These three settings are the only protections that survive into a git command the
    /// Hub did not launch, which is precisely why something inside the worktree would want
    /// them gone. Drift is a refusal and never a repair: writing them back and carrying on
    /// would erase the evidence that something tried, and would hand the next dangerous git
    /// command a worktree there is no longer any reason to trust.
    /// </summary>
    private async Task<List<RepositoryIssue>> InspectWorkspaceHardeningAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var issues = new List<RepositoryIssue>();

        await AssertHardeningAsync(
            workspacePath,
            GitConfigKeys.HooksPath,
            value => PathsLookEqual(value, _hooks.HooksDirectory),
            $"the Hub hooks directory '{_hooks.HooksDirectory}'",
            issues,
            cancellationToken);

        await AssertHardeningAsync(
            workspacePath,
            GitConfigKeys.PushUrl,
            value => string.Equals(value, GitConfigKeys.DisabledPushUrl, StringComparison.Ordinal),
            $"'{GitConfigKeys.DisabledPushUrl}'",
            issues,
            cancellationToken);

        await AssertHardeningAsync(
            workspacePath,
            GitConfigKeys.CredentialHelper,
            string.IsNullOrWhiteSpace,
            "an empty value, which resets the inherited helper list",
            issues,
            cancellationToken);

        return issues;
    }

    private async Task AssertHardeningAsync(
        string workspacePath,
        string key,
        Func<string, bool> isExpected,
        string expectation,
        List<RepositoryIssue> issues,
        CancellationToken cancellationToken)
    {
        var setting = await ReadWorktreeSettingAsync(workspacePath, key, cancellationToken);

        if (!setting.Read)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.UnsafeGitConfiguration,
                $"worktree hardening '{key}' could not be read, so it could not be confirmed "
                + $"({setting.Detail})"));
            return;
        }

        // Exactly one value: a second entry is a second answer, and git would not
        // necessarily be using the one that was inspected.
        if (setting.Values.Count != 1 || !isExpected(setting.Values[0]))
        {
            var observed = setting.Values.Count == 0
                ? "it is not set"
                : "it is " + string.Join(", ", setting.Values.Select(v => $"'{v}'"));

            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.UnsafeGitConfiguration,
                $"worktree hardening '{key}' no longer holds {expectation}: {observed}. "
                + "The workspace is refused; hardening is never rewritten and the change is never repaired"));
        }
    }

    /// <summary>
    /// One worktree scoped setting, as values rather than as text.
    ///
    /// An absent key and an unreadable scope are different answers and stay different: git
    /// exits 1 with an empty stderr for "not set", and an empty VALUE - which is what a
    /// reset credential helper looks like - is a real value that must survive the read.
    /// </summary>
    private async Task<(bool Read, IReadOnlyList<string> Values, string Detail)> ReadWorktreeSettingAsync(
        string workspacePath,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await GitAsync(
            workspacePath,
            ["config", WorktreeConfigurationScope, "--get-all", key],
            cancellationToken);

        if (result.Succeeded)
        {
            var values = result.Stdout
                .Split('\n')
                .Select(v => v.TrimEnd('\r'))
                .ToList();

            // The trailing newline of the last value is not a further, empty value.
            if (values.Count > 0 && values[^1].Length == 0)
            {
                values.RemoveAt(values.Count - 1);
            }

            return (true, values, string.Empty);
        }

        if (result.Started && result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr))
        {
            return (true, [], "not set");
        }

        return (false, [], result.Describe());
    }

    /// <summary>Configuration families the MVP refuses outright.</summary>
    public static IReadOnlyList<string> UnsupportedConfigurationFamilies { get; } =
        ["include.", "includeif.", "filter."];

    private static string UnsupportedConfigurationReason(string family) => family switch
    {
        "filter." => "a filter driver can run clean, smudge or process commands during checkout and staging",
        _ => "an included configuration file could import unsupported executable configuration",
    };

    /// <summary>
    /// Scans every tracked .gitattributes reachable from the pinned commit, including
    /// nested directories, for filter attributes. Nothing from the working tree is
    /// executed while doing so.
    ///
    /// This is not a gitattributes parser: it looks for explicit filter=&lt;driver&gt;
    /// tokens, which is what the MVP needs to fail closed.
    /// </summary>
    private async Task<AttributeInspection> InspectTrackedAttributesAsync(
        string repository,
        string commit,
        CancellationToken cancellationToken)
    {
        var issues = new List<RepositoryIssue>();
        var usesLfs = false;

        var listed = await GitAsync(
            repository,
            ["ls-tree", "-r", "--name-only", "-z", commit],
            cancellationToken);

        if (!listed.Succeeded)
        {
            return new AttributeInspection(false, issues);
        }

        var attributeFiles = listed.Stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(AttributesFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in attributeFiles)
        {
            var content = await GitAsync(repository, ["show", commit + ":" + file], cancellationToken);
            if (!content.Succeeded)
            {
                continue;
            }

            foreach (var driver in ReadFilterDrivers(content.Stdout))
            {
                if (string.Equals(driver, LfsFilterDriver, StringComparison.OrdinalIgnoreCase))
                {
                    usesLfs = true;
                    issues.Add(new RepositoryIssue(
                        RepositoryIssueCode.GitLfsUnsupported,
                        $"'{file}' declares a Git LFS filter; LFS repositories are rejected in the MVP"));
                }
                else
                {
                    issues.Add(new RepositoryIssue(
                        RepositoryIssueCode.UnsafeGitConfiguration,
                        $"'{file}' declares filter='{driver}'; custom filter drivers are not supported"));
                }
            }
        }

        return new AttributeInspection(usesLfs, issues);
    }

    private static IEnumerable<string> ReadFilterDrivers(string attributes)
    {
        foreach (var raw in attributes.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("filter=", StringComparison.OrdinalIgnoreCase))
                {
                    var driver = token["filter=".Length..].Trim();
                    if (driver.Length > 0)
                    {
                        yield return driver;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Re-runs every safety inspection immediately before an operation that could invoke a
    /// configured helper. Everything inspected when the workspace was created is mutable,
    /// and the agent that has been working in it is exactly who could have mutated it.
    ///
    /// The order below is protection, not presentation.
    ///
    /// The working tree scan runs first because it needs no git at all. The git reads that
    /// follow only read configuration and the pinned commit; none of them consults the
    /// working tree, so none of them can invoke a filter, a hook or a helper. The commands
    /// that CAN are the caller's, and the caller only reaches them when this returns null.
    ///
    /// Repository scope answers for what every worktree inherits. Worktree scope answers
    /// for what this one worktree was allowed to change. Hardening integrity answers for
    /// whether the protections that outlive a Hub command are still the ones this module
    /// wrote. Three different questions, none of which the other two can answer.
    /// </summary>
    private async Task<string?> RevalidateSafetyAsync(
        PreparedWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var issues = InspectWorkingTreeAttributes(workspace.WorkspacePath);

        issues.AddRange(await InspectRepositorySafetyAsync(
            workspace.RepositoryPath, workspace.BaseCommitSha, cancellationToken));

        issues.AddRange(await InspectWorktreeConfigurationAsync(workspace.WorkspacePath, cancellationToken));
        issues.AddRange(await InspectWorkspaceHardeningAsync(workspace.WorkspacePath, cancellationToken));

        return Describe(issues);
    }

    /// <summary>
    /// The repository scoped half, addressed by repository and pinned commit rather than by
    /// a workspace, so the boundary before a worktree exists can use the same one as the
    /// boundaries after it.
    /// </summary>
    private async Task<string?> RevalidateSafetyAsync(
        string repository,
        string commit,
        CancellationToken cancellationToken) =>
        Describe(await InspectRepositorySafetyAsync(repository, commit, cancellationToken));

    private async Task<List<RepositoryIssue>> InspectRepositorySafetyAsync(
        string repository,
        string commit,
        CancellationToken cancellationToken)
    {
        var issues = await InspectRepositoryConfigurationAsync(repository, cancellationToken);
        var attributes = await InspectTrackedAttributesAsync(repository, commit, cancellationToken);

        issues.AddRange(attributes.Issues);
        return issues;
    }

    private static string? Describe(List<RepositoryIssue> issues) =>
        issues.Count == 0 ? null : string.Join("; ", issues.Select(i => i.ToString()));

    /// <summary>
    /// Filesystem owned scan of the working tree's own attribute files.
    ///
    /// <see cref="InspectTrackedAttributesAsync" /> reads the pinned commit, which is the
    /// right authority for a repository nobody has worked in yet and the wrong one for a
    /// worktree an agent has had write access to: a file created after the pin is in no
    /// commit, and a filter attribute in it is enough to have git run a program the next
    /// time the Hub touches the working tree.
    ///
    /// Git is deliberately not used to find the files. Asking git to walk the working tree
    /// is itself one of the operations whose safety is the question here.
    ///
    /// Every candidate goes through the managed path boundary, so a junction that leaves
    /// the worktree is refused rather than followed - and the refusal is a finding, because
    /// an attribute file nobody could safely read is not an attribute file nobody has to
    /// worry about. Every bound below rejects rather than truncates for the same reason.
    /// </summary>
    private List<RepositoryIssue> InspectWorkingTreeAttributes(string workspacePath)
    {
        var issues = new List<RepositoryIssue>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = _options.AttributeScanMaxDepth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false,
        };

        List<string> candidates;

        try
        {
            candidates =
            [
                .. Directory
                    .EnumerateFiles(workspacePath, AttributesFileName, options)
                    .Take(_options.MaxAttributeFilesScanned + 1),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.UnsafeGitConfiguration,
                $"the working tree could not be scanned for '{AttributesFileName}' files, so it could not be "
                + $"checked for filter attributes ({ex.Message})"));

            return issues;
        }

        if (candidates.Count > _options.MaxAttributeFilesScanned)
        {
            issues.Add(new RepositoryIssue(
                RepositoryIssueCode.UnsafeGitConfiguration,
                $"the working tree holds more than {_options.MaxAttributeFilesScanned} "
                + $"'{AttributesFileName}' files; it is refused rather than partly scanned"));

            return issues;
        }

        foreach (var candidate in candidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Inside(workspacePath, candidate);
            var decision = _paths.Validate(workspacePath, candidate, mustExist: true);

            if (!decision.IsAllowed || decision.CanonicalPath is null)
            {
                issues.Add(new RepositoryIssue(
                    RepositoryIssueCode.UnsafeGitConfiguration,
                    $"'{relative}' does not resolve inside the managed worktree, so its filter attributes "
                    + $"could not be checked ({decision.Reason ?? "path refused"})"));
                continue;
            }

            string content;

            try
            {
                var info = new FileInfo(decision.CanonicalPath);

                if (info.Length > _options.MaxAttributeFileBytes)
                {
                    issues.Add(new RepositoryIssue(
                        RepositoryIssueCode.UnsafeGitConfiguration,
                        $"'{relative}' is {info.Length} bytes, past the "
                        + $"{_options.MaxAttributeFileBytes} byte limit for an attribute file; it is refused unread"));
                    continue;
                }

                content = File.ReadAllText(decision.CanonicalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new RepositoryIssue(
                    RepositoryIssueCode.UnsafeGitConfiguration,
                    $"'{relative}' could not be read, so its filter attributes could not be checked ({ex.Message})"));
                continue;
            }

            foreach (var driver in ReadFilterDrivers(content))
            {
                issues.Add(string.Equals(driver, LfsFilterDriver, StringComparison.OrdinalIgnoreCase)
                    ? new RepositoryIssue(
                        RepositoryIssueCode.GitLfsUnsupported,
                        $"the working tree file '{relative}' declares a Git LFS filter; "
                        + "LFS repositories are rejected in the MVP")
                    : new RepositoryIssue(
                        RepositoryIssueCode.UnsafeGitConfiguration,
                        $"the working tree file '{relative}' declares filter='{driver}'; "
                        + "custom filter drivers are not supported"));
            }
        }

        return issues;
    }

    /// <summary>
    /// A path spelled relative to the root for a message. Plain string work: it makes no
    /// trust decision and is never used to reach the filesystem.
    /// </summary>
    private static string Inside(string root, string path) =>
        path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart(Path.DirectorySeparatorChar, '/')
            : path;

    private sealed record AttributeInspection(bool UsesLfs, List<RepositoryIssue> Issues);

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
