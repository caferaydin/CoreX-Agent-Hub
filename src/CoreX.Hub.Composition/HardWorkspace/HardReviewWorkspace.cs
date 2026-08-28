using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// The review tree for a hard organization, inside the environment rather than beside it.
///
/// Review was classified mechanically rather than by reputation, and it came out on the wrong side
/// of the boundary: it materialises a worktree at the result commit and then runs a process with
/// that tree as its working directory. The tree is repository content, so the process is a process
/// operating on attacker supplied input, and a checkout is itself the classic place repository
/// controlled code gets to run. Doing that on Windows for a hard organization would reopen the
/// co-tenancy threat one step after verification closed it.
///
/// So the snapshot is Linux native and comes from the repository already inside the environment.
/// Nothing is transferred from the host again: the pinned source arrived once, through the one
/// path whose meaning is committed git objects, and re-sending it here would be a second way in
/// with weaker semantics. What this does is ask the environment's own controlled bare repository
/// for a detached tree at a commit the Hub chose.
///
/// Every git command runs as the trusted service identity with the Hub's hardened configuration -
/// no repository hooks path, no repository global config - which is what stops a checkout from
/// executing anything. The reviewer that runs afterwards is untrusted and is placed in the
/// execution's own namespace and mount sandbox by the lease, exactly as the agent and the
/// verification commands are.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardReviewWorkspace : IReviewWorkspace
{
    private readonly HardWorkspaceControl _control;
    private readonly HardWorktreeAuthority _worktrees;
    private readonly Isolation.IHardExecutionBinding _binding;

    /// <summary>
    /// Which controlled repository each live snapshot came from.
    ///
    /// The review port asks for a tree with a repository and gives it back without one, so the
    /// answer is remembered rather than re-derived. Pruning against the wrong repository would
    /// silently do nothing and leave a tree behind.
    /// </summary>
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);

    public HardReviewWorkspace(HardWorkspaceControl control, Isolation.IHardExecutionBinding binding)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _worktrees = new HardWorktreeAuthority(control);
    }

    /// <summary>
    /// Builds a detached tree at exactly the commit the Hub names.
    ///
    /// The commit is the Hub's result commit and never a branch: a name could move between being
    /// written down and being checked out, and a review of a different tree than the one that was
    /// accepted is worse than no review.
    /// </summary>
    public async Task<ReviewSnapshot?> PrepareAsync(
        Guid executionId,
        Guid repositoryId,
        string commitSha,
        string snapshotId,
        CancellationToken ct)
    {
        // The execution this tree belongs to, checked against everything else this composition has
        // routed. A review tree built for a different execution than the agent ran in would be a
        // review of somebody else's work carrying this execution's verdict.
        _binding.BindTo(executionId);

        if (!HardCommitSha.IsWellFormed(commitSha))
        {
            return null;
        }

        var bare = _control.Layout.BareRepository(repositoryId);
        var snapshot = SnapshotPath(snapshotId);

        // Removed first, so a directory left by an earlier attempt cannot be reviewed as though it
        // were this one. The name is Hub derived, so this can never name anything else.
        await _worktrees.DisposeAsync(bare, snapshot, ct);

        // The git command belongs to the worktree authority, which is the only place on this side
        // that builds one. It checks out detached at exactly this commit and reads HEAD back; a
        // tree that came up anywhere else is not a snapshot of the result.
        if (!await _worktrees.AddDetachedAsync(bare, snapshot, commitSha, ct))
        {
            return null;
        }

        _origins[snapshot] = bare;

        return new ReviewSnapshot(snapshot, commitSha);
    }

    /// <summary>
    /// What the tree looks like right now, in the neutral terms the engine compares.
    ///
    /// Called before the reviewer runs and again after every attempt, which is how a reviewer that
    /// modified what it was measuring is caught. Nothing here interprets the answer - the engine
    /// owns that rule, and it is frozen.
    /// </summary>
    public async Task<ReviewSnapshotIntegrity?> InspectAsync(ReviewSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var head = HardWorkspaceControl.FirstLine(
            await _control.ServiceAsync(
                HardGitCommands.Hardened(_control.Layout, "-C", snapshot.SnapshotPath, "rev-parse", "HEAD"), ct));

        if (head is not { Length: > 0 })
        {
            return null;
        }

        var tree = HardWorkspaceControl.FirstLine(
            await _control.ServiceAsync(
                HardGitCommands.Hardened(
                    _control.Layout, "-C", snapshot.SnapshotPath, "rev-parse", "HEAD^{tree}"), ct));

        if (tree is not { Length: > 0 })
        {
            return null;
        }

        var status = await _control.ServiceAsync(
            HardGitCommands.Hardened(
                _control.Layout, "-C", snapshot.SnapshotPath, "status", "--porcelain"), ct);

        if (!status.Ok)
        {
            return null;
        }

        var dirty = status.Lines.Where(l => l.Trim().Length > 0).ToList();

        return new ReviewSnapshotIntegrity(
            head,
            tree,
            dirty.Count == 0,
            dirty.Count == 0
                ? "the review tree is clean"
                : $"the review tree has {dirty.Count} modified path(s)");
    }

    /// <summary>
    /// Takes the tree away, and says so when it will not go.
    ///
    /// A residual is reported rather than retried into silence: a tree that outlives its review is
    /// worth an operator knowing about, and it is never a review finding.
    /// </summary>
    public async Task<string?> ReleaseAsync(ReviewSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_origins.TryGetValue(snapshot.SnapshotPath, out var bare))
        {
            // A snapshot this instance did not prepare. Nothing here knows it is Hub owned, and a
            // path arriving from somewhere unaccounted for is preserved rather than removed.
            return $"the review snapshot at '{snapshot.SnapshotPath}' was not prepared here; it is left as it is";
        }

        _origins.Remove(snapshot.SnapshotPath);

        // Disposal, not repair. The authority removes the directory it created and prunes the
        // administrative record; there is deliberately no --force anywhere on this path, because a
        // disposal that carries one is a single edit away from becoming a way to erase evidence.
        return await _worktrees.DisposeAsync(bare, snapshot.SnapshotPath, ct);
    }

    /// <summary>
    /// Where a review tree lives, derived from the Hub's own snapshot identity.
    ///
    /// Beneath the execution root and named from an identifier the Hub composed, so no display
    /// name, task key or repository string reaches the filesystem.
    /// </summary>
    private string SnapshotPath(string snapshotId) =>
        _control.Layout.ExecutionRoot + "/reviews/" + Sanitised(snapshotId);

    private static string Sanitised(string snapshotId) =>
        new([.. snapshotId.Where(char.IsAsciiLetterOrDigit)]);
}

/// <summary>
/// Whether a string is a commit identifier and not something else wearing the argument slot.
///
/// The commit reaches a git argument vector, so it is checked for what it is rather than trusted
/// for where it came from: forty hexadecimal characters, and nothing that could begin an option.
/// </summary>
internal static class HardCommitSha
{
    public static bool IsWellFormed(string? sha) =>
        sha is { Length: >= 7 and <= 64 } && sha.All(char.IsAsciiHexDigit);

    public static string Identity(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}
