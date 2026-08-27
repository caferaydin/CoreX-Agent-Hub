using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Workspace;
using CoreX.Hub.Workspace.Abstractions;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// A workspace that lives inside a hard isolated environment.
///
/// It answers the same Application port the Windows workspace answers, and that is the whole
/// shape of this slice: the orchestrator sequences a pipeline and never learns which side of the
/// boundary the worktree is on. There is no <c>if (hard)</c> anywhere above this line, and the
/// existing Windows implementation is untouched.
///
/// What it does not share is the machine. The controlled repository, the worktree and every git
/// command are on a Linux filesystem inside the environment; the Windows repository is the source
/// of a pinned commit and nothing else, and the agent never sees it. The source is read once, by
/// the trusted host side git, and what crosses is committed objects.
///
/// The preflight is deliberately the existing one. A second, hard-specific inspection would be a
/// second policy about submodules, filters, LFS and unsafe configuration - and the weaker of two
/// policies is the one that decides. So a repository is judged by the rules it has always been
/// judged by, on the host, before anything is transferred.
///
/// This exists as a primitive. Nothing composes it into an execution: a Hub task also needs its
/// context, its evidence, its provider authentication and its network on the right side of the
/// boundary, and until those move a task running here would be half outside it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardWorkspaceGateway : IExecutionWorkspace
{
    private readonly WorkspaceManager _windows;
    private readonly Func<Guid, RepositoryLocation> _locate;
    private readonly HardWorkspaceControl _control;
    private readonly PinnedTransfer _transfer;
    private readonly Dictionary<string, HardWorkspaceHandle> _prepared = new(StringComparer.Ordinal);

    internal HardWorkspaceGateway(
        WorkspaceManager windows,
        Func<Guid, RepositoryLocation> locate,
        HardWorkspaceControl control,
        PinnedTransfer transfer)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
    }

    /// <summary>
    /// Builds the hard workspace for one organization's environment.
    ///
    /// <paramref name="hostStagingRoot"/> is the Hub's own data directory. Transfer artefacts are
    /// written there for as long as one takes to encode, and nowhere near a worktree.
    /// </summary>
    public static HardWorkspaceGateway Create(
        WorkspaceManager windows,
        Func<Guid, RepositoryLocation> locate,
        IGitCommandExecutor hostGit,
        HardIsolationProfile profile,
        string hostStagingRoot,
        string hostWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(profile);

        if (!WslExecutionGateway.TryCreate(hostWorkingDirectory, out var gateway, out var problem))
        {
            throw new InvalidOperationException(
                $"a hard workspace needs a reachable environment: {problem}");
        }

        var layout = HardWorkspaceLayout.For(profile.ExecutionRoot);

        return new HardWorkspaceGateway(
            windows,
            locate,
            new HardWorkspaceControl(gateway!, profile, layout),
            new PinnedTransfer(
                hostGit,
                Path.Combine(hostStagingRoot, "hard-transfer"),
                windows.Hooks.EmptyGlobalConfigPath,
                windows.Hooks.HooksDirectory));
    }

    /// <summary>A gateway over a scripted environment, for this assembly's tests.</summary>
    internal static HardWorkspaceGateway Scripted(
        WorkspaceManager windows,
        Func<Guid, RepositoryLocation> locate,
        IWslExecutionGateway gateway,
        HardIsolationProfile profile,
        PinnedTransfer transfer) =>
        new(
            windows,
            locate,
            new HardWorkspaceControl(gateway, profile, HardWorkspaceLayout.For(profile.ExecutionRoot)),
            transfer);

    /// <summary>
    /// The existing repository inspection, on the host, unchanged.
    ///
    /// Nothing about the environment is consulted here. A repository with a submodule, an LFS
    /// filter, an unsafe configuration or an unresolvable branch is refused before a single byte
    /// is transferred, by the same policy that refuses it for a Windows execution.
    /// </summary>
    public async Task<IExecutionWorkspacePreflight> PreflightAsync(Guid repositoryId, CancellationToken ct)
    {
        var location = _locate(repositoryId);

        var preflight = await _windows.PreflightAsync(
            location.RepositoryPath, location.RepositoryTrustRoot, location.DefaultBranch, ct);

        return new HardPinnedPreflight(location, preflight);
    }

    public async Task<PreparedExecutionWorkspace?> PrepareAsync(
        Guid repositoryId,
        IExecutionWorkspacePreflight preflight,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        if (preflight is not HardPinnedPreflight pinned)
        {
            throw new InvalidOperationException(
                $"'{preflight.GetType().Name}' was not produced by this gateway, so the commit it names "
                + "cannot be shown to have come from this repository.");
        }

        if (pinned.BaseCommitSha is not { Length: 40 } baseCommit)
        {
            return null;
        }

        var location = _locate(repositoryId);
        var layout = _control.Layout;

        // A fresh identity per attempt. Two attempts of one task, and two tasks of one
        // repository, never derive the same directory.
        var worktreeIdentity = Guid.CreateVersion7();
        var worktree = layout.Worktree(worktreeIdentity);
        var bare = layout.BareRepository(repositoryId);
        var branch = $"agent/{taskKey}/{attemptNo}";

        if (!await EnsureLayoutAsync(ct))
        {
            return null;
        }

        var transferred = await _transfer.CreateAsync(
            location.RepositoryPath, location.DefaultBranch, baseCommit, ct);

        if (!transferred.Succeeded)
        {
            return null;
        }

        var artefact = layout.TransferArtefact(worktreeIdentity);

        try
        {
            if (!await ImportAsync(bare, artefact, transferred.Encoded, baseCommit, ct))
            {
                return null;
            }
        }
        finally
        {
            // The artefact has done its work either way. It carries only committed objects, and
            // it still does not stay.
            await _control.ServiceAsync([HardGitCommands.Rm, "-f", artefact], CancellationToken.None);
        }

        if (!await CreateWorktreeAsync(bare, worktree, branch, baseCommit, ct))
        {
            return null;
        }

        if (!await ApplyOwnershipAsync(bare, worktree, ct))
        {
            return null;
        }

        var handle = new HardWorkspaceHandle(worktree, branch, baseCommit, bare);
        _prepared[worktree] = handle;

        return new PreparedExecutionWorkspace(worktree, branch, baseCommit);
    }

    /// <summary>
    /// Everything the Hub owns inside the environment, created once and owned by the service.
    ///
    /// The hooks directory is empty and stays empty. It is what every managed command's
    /// <c>core.hooksPath</c> points at, which is how a repository that configures its own hooks
    /// finds that its hooks are not the ones git runs - including during the first checkout.
    /// </summary>
    private async Task<bool> EnsureLayoutAsync(CancellationToken ct)
    {
        var layout = _control.Layout;

        var made = await _control.ServiceAsync(
            [
                HardGitCommands.Mkdir, "-p",
                layout.HooksDirectory, layout.StageRoot, layout.RepositoriesRoot, layout.WorktreesRoot,
            ],
            ct);

        if (!made.Ok)
        {
            return false;
        }

        var config = await _control.ServiceAsync(
            [HardGitCommands.Touch, layout.EmptyGlobalConfigPath], ct);

        if (!config.Ok)
        {
            return false;
        }

        // The agent has no business anywhere but its own worktree, so nothing else is readable
        // to it at all. The worktree is opened up individually, afterwards.
        var closed = await _control.OwnershipAsync(
            [HardGitCommands.Chmod, "-R", "o-rwx", layout.ControlRoot, layout.StageRoot, layout.RepositoriesRoot],
            ct);

        return closed.Ok;
    }

    /// <summary>
    /// Receives the pinned objects into a Hub-created repository.
    ///
    /// The repository is initialised here rather than cloned from the artefact, and its
    /// configuration is therefore the Hub's. The source repository's own <c>.git/config</c> is
    /// never transferred and never consulted: committed content is untrusted, and a developer
    /// machine's git configuration is not content the Hub has any reason to adopt.
    ///
    /// No remote is added. Nothing here can fetch or push, and there is nothing to fetch from.
    /// </summary>
    private async Task<bool> ImportAsync(
        string bare,
        string artefact,
        string encoded,
        string baseCommit,
        CancellationToken ct)
    {
        var decoded = await _control.ServiceAsync(
            HardGitCommands.DecodeTransfer(artefact), ct, stdin: encoded);

        if (!decoded.Ok)
        {
            return false;
        }

        // Asked again on this side. The host verified what it packaged; this verifies what
        // arrived, which is a different claim and the one that matters here.
        var heads = await _control.ServiceAsync(HardGitCommands.ListTransferHeads(artefact), ct);

        if (!heads.Ok || !heads.Lines.Any(l => l.StartsWith(baseCommit, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var initialised = await _control.GitAsync(ct, "init", "--bare", "--initial-branch=main", bare);

        if (!initialised.Ok)
        {
            return false;
        }

        var fetched = await _control.GitAsync(
            ct, "--git-dir=" + bare, "fetch", artefact, "refs/heads/*:refs/heads/*");

        if (!fetched.Ok)
        {
            return false;
        }

        var resolved = HardWorkspaceControl.FirstLine(
            await _control.GitAsync(ct, "--git-dir=" + bare, "rev-parse", "--verify", baseCommit + "^{commit}"));

        return string.Equals(resolved, baseCommit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the worktree at exactly the pinned commit, on a Hub-created branch.
    ///
    /// One command, and it is the first checkout-capable one - which is why the hardened
    /// configuration is already in force when it runs rather than applied afterwards. A
    /// repository that names its own hooks path gets to execute code during checkout, and
    /// "harden after checking out" would be hardening after the thing it was meant to prevent.
    /// </summary>
    private async Task<bool> CreateWorktreeAsync(
        string bare,
        string worktree,
        string branch,
        string baseCommit,
        CancellationToken ct)
    {
        var added = await _control.GitAsync(
            ct, "--git-dir=" + bare, "worktree", "add", "-b", branch, worktree, baseCommit);

        if (!added.Ok)
        {
            return false;
        }

        var head = HardWorkspaceControl.FirstLine(await _control.GitAsync(ct, "-C", worktree, "rev-parse", "HEAD"));

        return string.Equals(head, baseCommit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ownership model, which is where the boundary actually lives.
    ///
    /// The checked out files become the agent's, because it has to edit them. Everything that
    /// decides what a repository is - the object database, the refs, the configuration, the
    /// worktree administration data and the <c>.git</c> pointer that names it - stays with the
    /// service user.
    ///
    /// The worktree directory itself is the subtle part. It has to be writable by the agent, or
    /// no new file could be created; and a writable directory normally lets anyone delete
    /// anything in it, including the <c>.git</c> pointer. The sticky bit is what separates those:
    /// with it set, an entry may only be removed by whoever owns it. The agent creates and
    /// removes its own files and cannot touch the service's.
    ///
    /// Group rather than world permissions, so a second organization's account on the same
    /// machine is not invited in by the same change.
    /// </summary>
    private async Task<bool> ApplyOwnershipAsync(string bare, string worktree, CancellationToken ct)
    {
        var agent = _control.Profile.AgentUser.Trim();
        var service = _control.Profile.ServiceUser.Trim();

        var repository = await _control.OwnershipAsync(
            [HardGitCommands.Chown, "-R", service, bare], ct);

        if (!repository.Ok)
        {
            return false;
        }

        var closed = await _control.OwnershipAsync([HardGitCommands.Chmod, "-R", "o-rwx", bare], ct);

        if (!closed.Ok)
        {
            return false;
        }

        var tree = await _control.OwnershipAsync([HardGitCommands.Chown, "-R", agent, worktree], ct);

        if (!tree.Ok)
        {
            return false;
        }

        var control = await _control.OwnershipAsync(
            [HardGitCommands.Chown, service + ":" + agent, worktree, worktree + "/.git"], ct);

        if (!control.Ok)
        {
            return false;
        }

        var pointer = await _control.OwnershipAsync([HardGitCommands.Chmod, "0640", worktree + "/.git"], ct);

        if (!pointer.Ok)
        {
            return false;
        }

        var sticky = await _control.OwnershipAsync([HardGitCommands.Chmod, "1770", worktree], ct);

        return sticky.Ok;
    }

    /// <summary>What the worktree holds after the agent finished, collected by the service.</summary>
    public async Task<ExecutionCapture> CaptureAsync(PreparedExecutionWorkspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var handle = Handle(workspace);
        var worktree = handle.WorktreePath;

        var head = HardWorkspaceControl.FirstLine(
            await _control.GitAsync(ct, "-C", worktree, "rev-parse", "HEAD")) ?? string.Empty;

        var status = await _control.GitAsync(
            ct, "-C", worktree, "status", "--porcelain=v2", "--untracked-files=all", "--branch");

        var graph = await _control.GitAsync(
            ct, "-C", worktree, "log", "--format=%H%x09%an%x09%aI%x09%s", handle.BaseCommitSha + "..HEAD");

        var unexpected = await _control.GitAsync(
            ct, "-C", worktree, "rev-list", handle.BaseCommitSha + "..HEAD");

        // Intent to add makes new files visible to diff without staging their content and
        // without creating a commit. Ignored files stay out, so a gitignored secret never
        // reaches the evidence.
        await _control.GitAsync(ct, "-C", worktree, "add", "-N", ".");

        var diff = await _control.GitAsync(
            ct, "-C", worktree, "diff", HardGitCommands.NoExternalDiff, HardGitCommands.NoTextConv,
            handle.BaseCommitSha);

        var nameStatus = await _control.GitAsync(
            ct, "-C", worktree, "diff", HardGitCommands.NoExternalDiff, HardGitCommands.NoTextConv,
            "--name-status", handle.BaseCommitSha);

        return new ExecutionCapture
        {
            BaseCommitSha = handle.BaseCommitSha,
            ObservedHeadSha = head,
            UnexpectedCommitShas = unexpected.Ok ? unexpected.Lines : [],
            GitStatus = status.Stdout,
            GitCommitGraph = graph.Stdout,
            GitWorktreeDiff = diff.Stdout,
            ChangedPaths = ChangedPaths(nameStatus.Stdout),
        };
    }

    /// <summary>
    /// The Hub's own commit, created by the service identity and by nothing else.
    ///
    /// The agent could not have made it: it has no access to the object database, so the
    /// question of whether it was allowed to does not arise. What this preserves from the Windows
    /// side is the identity - a result commit stays mechanically attributable to the Hub - and
    /// the absence of any history rewriting: no amend, no reset, no force.
    ///
    /// It decides nothing about whether a commit should exist. That belongs to the gates, and a
    /// workspace that approved its own output would be the wrong shape entirely.
    /// </summary>
    public async Task<ResultCommitOutcomeView> CreateResultCommitAsync(
        PreparedExecutionWorkspace workspace,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var handle = Handle(workspace);
        var worktree = handle.WorktreePath;

        var staged = await _control.GitAsync(ct, "-C", worktree, "add", "-A");

        if (!staged.Ok)
        {
            return new ResultCommitOutcomeView(false, null, null, staged.Stderr.Trim(), [], true);
        }

        var pending = await _control.GitAsync(ct, "-C", worktree, "diff", "--cached", "--name-only");

        if (!pending.Ok)
        {
            return new ResultCommitOutcomeView(false, null, null, pending.Stderr.Trim(), [], false);
        }

        if (pending.Lines.Count == 0)
        {
            return new ResultCommitOutcomeView(false, null, null, "nothing to commit", [], true);
        }

        var message = $"corex: {taskKey} attempt {attemptNo}";
        var committed = await _control.GitAsHubAsync(ct, "-C", worktree, "commit", "-m", message);

        if (!committed.Ok)
        {
            return new ResultCommitOutcomeView(false, null, null, committed.Stderr.Trim(), [], true);
        }

        var sha = HardWorkspaceControl.FirstLine(await _control.GitAsync(ct, "-C", worktree, "rev-parse", "HEAD"));

        var diff = await _control.GitAsync(
            ct, "-C", worktree, "show", HardGitCommands.NoExternalDiff, HardGitCommands.NoTextConv, "--format=", sha!);

        var remaining = await _control.GitAsync(
            ct, "-C", worktree, "status", "--porcelain=v2", "--untracked-files=all");

        return new ResultCommitOutcomeView(
            true,
            sha,
            diff.Stdout,
            $"result commit {sha} on {handle.BranchName}",
            remaining.Ok ? remaining.Lines : [],
            remaining.Ok);
    }

    /// <summary>
    /// Whether a path the Hub is about to touch is really inside the worktree.
    ///
    /// Offered as its own operation because the answer needs the machine. The agent may create
    /// symlinks - that is an ordinary thing for source code to contain - and a link inside the
    /// worktree can name a target anywhere while every character of its path stays under the
    /// root. The Hub resolves first and decides on what it resolved to.
    /// </summary>
    public Task<bool> IsInsideWorktreeAsync(
        PreparedExecutionWorkspace workspace,
        string candidate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return _control.IsInsideAsync(Handle(workspace).WorktreePath, candidate, ct);
    }

    /// <summary>
    /// Removes the attempt's worktree, and only the attempt's worktree.
    ///
    /// The controlled repository stays: it holds the branch the result commit is on, and that is
    /// the provenance an acceptance decision is about. Removing it because the worktree is gone
    /// would destroy the record of what happened along with the place it happened.
    /// </summary>
    public async Task<HardWorkspaceReleaseOutcome> ReleaseAsync(
        PreparedExecutionWorkspace workspace,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var handle = Handle(workspace);

        var removed = await _control.GitAsync(
            ct, "--git-dir=" + handle.BareRepositoryPath, "worktree", "remove", handle.WorktreePath);

        if (!removed.Ok)
        {
            // Reported, never forced. A worktree that will not come away cleanly is a thing an
            // operator should look at, and "remove it harder" is how evidence disappears.
            return new HardWorkspaceReleaseOutcome(false, handle.WorktreePath, removed.Stderr.Trim());
        }

        _prepared.Remove(handle.WorktreePath);

        return new HardWorkspaceReleaseOutcome(true, handle.WorktreePath, "released");
    }

    private HardWorkspaceHandle Handle(PreparedExecutionWorkspace workspace) =>
        _prepared.TryGetValue(workspace.WorkspacePath, out var handle)
            ? handle
            : throw new InvalidOperationException(
                $"'{workspace.WorkspacePath}' was not prepared by this gateway, so the repository it "
                + "belongs to is not known.");

    private static IReadOnlyList<string> ChangedPaths(string nameStatus) =>
        [.. nameStatus
            .Split('\n')
            .Select(line => line.Trim('\r', ' '))
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t').Last())];

    /// <summary>What one prepared hard workspace is, beyond what the port carries.</summary>
    private sealed record HardWorkspaceHandle(
        string WorktreePath,
        string BranchName,
        string BaseCommitSha,
        string BareRepositoryPath);

    /// <summary>The host side preflight, carried back unopened.</summary>
    private sealed class HardPinnedPreflight(RepositoryLocation location, global::CoreX.Hub.Workspace.RepositoryPreflightResult result)
        : IExecutionWorkspacePreflight
    {
        public RepositoryLocation Location { get; } = location;

        public global::CoreX.Hub.Workspace.RepositoryPreflightResult Result { get; } = result;

        public string? BaseCommitSha => Result.IsUsable ? Result.BaseCommitSha : null;

        public string Detail => Result.Describe();
    }
}

/// <summary>What releasing one hard worktree did.</summary>
public sealed record HardWorkspaceReleaseOutcome(bool Released, string WorktreePath, string Detail);
