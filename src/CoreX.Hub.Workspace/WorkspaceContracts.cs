namespace CoreX.Hub.Workspace;

/// <summary>Why a repository cannot be used for managed execution.</summary>
public enum RepositoryIssueCode
{
    PathRejected,
    NotAGitRepository,
    BareRepository,
    IsAnotherWorktree,
    DefaultBranchMissing,
    DefaultBranchUnresolvable,
    SubmodulesUnsupported,
    GitLfsUnsupported,

    /// <summary>Repository-local configuration names an external program the MVP cannot run safely.</summary>
    UnsafeGitConfiguration,
    IncompatibleWorktreeConfiguration,
    GitCommandFailed,
}

/// <summary>Why a workspace could not be prepared.</summary>
public enum WorkspaceIssueCode
{
    RepositoryUnusable,
    WorkspacePathRejected,
    WorkspacePathAlreadyExists,
    BranchAlreadyExists,
    WorktreeAddFailed,
    HardeningFailed,
}

public sealed record RepositoryIssue(RepositoryIssueCode Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

public sealed record WorkspaceIssue(WorkspaceIssueCode Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

/// <summary>
/// Read-only inspection of a managed repository. Nothing is fetched, pulled, repaired or
/// written: preflight only looks (Phase 3 brief section 8).
/// </summary>
public sealed record RepositoryPreflightResult
{
    public required bool IsUsable { get; init; }

    public IReadOnlyList<RepositoryIssue> Issues { get; init; } = [];

    public string? CanonicalRepositoryPath { get; init; }

    public string? DefaultBranch { get; init; }

    /// <summary>The pinned commit every later worktree is created from.</summary>
    public string? BaseCommitSha { get; init; }

    public bool HasSubmodules { get; init; }

    public bool UsesGitLfs { get; init; }

    public bool IsBare { get; init; }

    public string Describe() =>
        IsUsable ? "usable" : string.Join("; ", Issues.Select(i => i.ToString()));
}

/// <summary>What the caller wants prepared. Plain data, no orchestration concepts.</summary>
public sealed record WorkspaceRequest
{
    public required string RepositoryPath { get; init; }

    /// <summary>Trust root the repository must live inside.</summary>
    public required string RepositoryTrustRoot { get; init; }

    /// <summary>Trust root the workspace must live inside; never inside the repository.</summary>
    public required string WorkspaceRoot { get; init; }

    public required string OrganizationSlug { get; init; }

    public required string RepositorySlug { get; init; }

    public required string TaskKey { get; init; }

    public required int AttemptNo { get; init; }

    public required string DefaultBranch { get; init; }

    /// <summary>agent/&lt;taskKey&gt;/&lt;attempt&gt; (architecture G.2).</summary>
    public string BranchName => $"agent/{TaskKey}/{AttemptNo}";

    /// <summary>&lt;workspaceRoot&gt;/&lt;org&gt;/&lt;repo&gt;/&lt;taskKey&gt;-&lt;attempt&gt; (architecture G.1).</summary>
    public string RelativeWorkspacePath =>
        Path.Combine(OrganizationSlug, RepositorySlug, $"{TaskKey}-{AttemptNo}");
}

/// <summary>A prepared, hardened agent worktree.</summary>
public sealed record PreparedWorkspace
{
    public required string RepositoryPath { get; init; }

    public required string WorkspacePath { get; init; }

    public required string BranchName { get; init; }

    public required string BaseCommitSha { get; init; }

    public required string HooksDirectory { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record WorkspacePreparationResult
{
    public required bool Succeeded { get; init; }

    public PreparedWorkspace? Workspace { get; init; }

    public IReadOnlyList<WorkspaceIssue> Issues { get; init; } = [];

    public RepositoryPreflightResult? Preflight { get; init; }

    public string Describe() =>
        Succeeded ? "prepared" : string.Join("; ", Issues.Select(i => i.ToString()));
}

/// <summary>One changed path in the workspace, with deterministic metadata.</summary>
public sealed record WorkspaceFileEntry(
    string RelativePath,
    string ChangeStatus,
    long SizeBytes,
    string? Sha256,
    bool Exists);

/// <summary>
/// The Phase 3 workspace evidence primitive. It reports git truth and nothing else:
/// it does not compute an execution outcome, and it runs no scope or secret gate.
/// </summary>
public sealed record CaptureChangesResult
{
    public required string BaseCommitSha { get; init; }

    public required string CurrentHeadSha { get; init; }

    /// <summary>True when HEAD moved away from the pinned base commit (errata E1 / K1).</summary>
    public bool UnexpectedAgentCommitDetected => !string.Equals(
        BaseCommitSha, CurrentHeadSha, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    /// <summary>git status --porcelain=v2 --untracked-files=all.</summary>
    public string GitStatus { get; init; } = string.Empty;

    /// <summary>git log BaseCommitSha..HEAD in a machine readable format.</summary>
    public string GitCommitGraph { get; init; } = string.Empty;

    /// <summary>Working tree diff against the base commit, including intent-to-add files.</summary>
    public string GitWorktreeDiff { get; init; } = string.Empty;

    public IReadOnlyList<WorkspaceFileEntry> FileInventory { get; init; } = [];

    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    /// <summary>
    /// Set when the safety preflight refused the repository. Every evidence field is then
    /// empty, because collecting it would have touched the working tree and could have
    /// invoked a repository configured filter.
    /// </summary>
    public string? SafetyRejection { get; init; }

    public bool HasChanges => ChangedPaths.Count > 0;
}

public enum ResultCommitState
{
    Created,

    /// <summary>
    /// The safety preflight refused the repository before anything was staged, so no
    /// repository configured helper could run.
    /// </summary>
    RepositoryUnsafe,

    /// <summary>Nothing to commit. An empty commit is never manufactured.</summary>
    NoChanges,

    Failed,
}

public sealed record ResultCommitOutcome
{
    public required ResultCommitState State { get; init; }

    public string? CommitSha { get; init; }

    public string? CommitDiff { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// What the worktree still holds that the commit did not take, reported the way
    /// <c>git worktree remove</c> would judge it: modified or untracked paths, ignored
    /// files excluded.
    ///
    /// Empty is the expected answer, because the commit staged everything. A non empty one
    /// says the tree is not the commit, which matters to anyone about to measure it or
    /// remove it.
    /// </summary>
    public IReadOnlyList<string> UncommittedPaths { get; init; } = [];

    /// <summary>
    /// Whether the tree could be inspected at all.
    ///
    /// Separate from an empty <see cref="UncommittedPaths"/> on purpose: "nothing is left"
    /// and "nobody could look" are different answers, and collapsing them would let an
    /// unreadable tree pass for a clean one.
    /// </summary>
    public bool WorktreeStateKnown { get; init; } = true;
}

public enum WorkspaceReleaseState
{
    Released,

    /// <summary>ADR-0016: no result commit means the worktree holds the only copy.</summary>
    DeniedNoResultCommit,

    /// <summary>Removal failed; the workspace is reported, never force deleted.</summary>
    Orphaned,
}

public sealed record WorkspaceReleaseResult
{
    public required WorkspaceReleaseState State { get; init; }

    public string? Detail { get; init; }

    public int RemovalAttempts { get; init; }
}

public enum WorkspaceHealth
{
    Healthy,

    /// <summary>Known to the Hub, but git no longer lists it.</summary>
    Missing,

    /// <summary>A worktree git knows about that the Hub does not.</summary>
    Orphaned,

    /// <summary>Listed by git but the directory is gone or unreadable.</summary>
    RemovalFailed,
}

public sealed record WorkspaceStatusEntry(
    string WorktreePath,
    string? BranchName,
    string? HeadSha,
    WorkspaceHealth Health,
    string Detail);

public sealed record ReconcileReport(IReadOnlyList<WorkspaceStatusEntry> Entries)
{
    public IReadOnlyList<WorkspaceStatusEntry> Orphaned =>
        [.. Entries.Where(e => e.Health == WorkspaceHealth.Orphaned)];

    public IReadOnlyList<WorkspaceStatusEntry> Missing =>
        [.. Entries.Where(e => e.Health == WorkspaceHealth.Missing)];
}

/// <summary>A workspace the Hub already knows about, supplied by the caller.</summary>
public sealed record KnownWorkspace(string WorktreePath, string? BranchName, bool HasResultCommit);

/// <summary>Why an exact commit snapshot could not be materialised.</summary>
public enum SnapshotIssueCode
{
    RepositoryUnusable,
    SnapshotPathRejected,
    SnapshotPathAlreadyExists,
    CommitNotFound,
    CommitShaMalformed,
    WorktreeAddFailed,
    HeadMismatch,
    HardeningFailed,
}

/// <summary>
/// Materialise a working tree at one exact commit that the caller already knows.
///
/// Deliberately a different request from <see cref="WorkspaceRequest"/>: an agent attempt
/// resolves a branch and creates <c>agent/&lt;task&gt;/&lt;attempt&gt;</c>, while this
/// resolves nothing at all. The two intentions stay mechanically distinct so no caller can
/// drift from one into the other.
///
/// Nothing about a task, an attempt, an agent, a provider or a verification profile
/// belongs here: this is a commit oriented primitive.
/// </summary>
public sealed record PinnedSnapshotRequest
{
    public required string RepositoryPath { get; init; }

    /// <summary>Trust root the repository must live inside.</summary>
    public required string RepositoryTrustRoot { get; init; }

    /// <summary>Hub owned root the snapshot must live inside; never inside the repository.</summary>
    public required string SnapshotRoot { get; init; }

    /// <summary>
    /// The exact commit to materialise. It is used as given: nothing is resolved, fetched
    /// or substituted, and a newer commit is never accepted in its place.
    /// </summary>
    public required string CommitSha { get; init; }

    /// <summary>Collision safe identity for this snapshot directory.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Snapshots live beside each other under one folder, apart from execution worktrees.</summary>
    public string RelativeSnapshotPath => Path.Combine(PinnedSnapshot.DirectoryName, SnapshotId);
}

/// <summary>A materialised, disposable working tree at one exact commit.</summary>
public sealed record PinnedSnapshot
{
    /// <summary>Folder that separates disposable snapshots from execution worktrees.</summary>
    public const string DirectoryName = "_snapshots";

    public required string RepositoryPath { get; init; }

    public required string SnapshotPath { get; init; }

    /// <summary>The commit the caller asked for.</summary>
    public required string CommitSha { get; init; }

    /// <summary>What git actually reports at HEAD. Equal to <see cref="CommitSha"/> or the snapshot was refused.</summary>
    public required string ObservedHeadSha { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record PinnedSnapshotResult
{
    public required bool Succeeded { get; init; }

    public PinnedSnapshot? Snapshot { get; init; }

    public IReadOnlyList<SnapshotIssue> Issues { get; init; } = [];

    public string Describe() =>
        Succeeded ? "snapshot at " + Snapshot!.CommitSha[..12] : string.Join("; ", Issues.Select(i => i.ToString()));
}

public sealed record SnapshotIssue(SnapshotIssueCode Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

/// <summary>Whether a snapshot is still pinned to the commit it was created at.</summary>
public sealed record PinnedSnapshotCheck(bool StillPinned, string? ObservedHeadSha, string? Detail);
