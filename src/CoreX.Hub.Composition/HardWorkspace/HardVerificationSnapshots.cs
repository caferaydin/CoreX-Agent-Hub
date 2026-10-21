using System.Runtime.Versioning;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>One disposable tree inside the environment, pinned to one commit.</summary>
internal sealed record HardPinnedSnapshot(string SnapshotPath, string CommitSha, string BareRepository);

/// <summary>
/// Throwaway trees for measuring a hard organization's commits, inside the environment.
///
/// The reason a verification run gets its own tree rather than the agent's is unchanged by
/// isolation, and it is two reasons at once. Verification fills a tree with build output, and a
/// worktree full of build output cannot be removed without forcing - which the Hub never does to a
/// worktree that may hold the only record of what an execution did. And a measurement of a
/// directory that happens to contain a commit is not a measurement of the commit.
///
/// What isolation changes is which filesystem the tree is on. A hard organization's tree is Linux
/// native and comes from the controlled bare repository already inside the environment - nothing is
/// transferred from the host a second time, because the pinned source arrived once through the one
/// path whose meaning is committed git objects.
///
/// Every command here belongs to the worktree authority, which is the only thing on this side that
/// creates or removes a worktree. There is no second creation site and no <c>--force</c> anywhere,
/// so a verification run cannot become a way to rewrite or erase what an execution produced.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardVerificationSnapshots
{
    private readonly HardWorkspaceControl _control;
    private readonly HardWorktreeAuthority _worktrees;

    public HardVerificationSnapshots(HardWorkspaceControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _worktrees = new HardWorktreeAuthority(control);
    }

    /// <summary>
    /// Builds a detached tree at exactly one commit, or returns null with the reason.
    ///
    /// Detached rather than on a branch: a name could move between being written down and being
    /// checked out, and a measurement of a different tree than the one that was accepted is worse
    /// than no measurement.
    /// </summary>
    public async Task<(HardPinnedSnapshot? Snapshot, string? Problem)> PrepareAsync(
        Guid repositoryId,
        string commitSha,
        string purpose,
        CancellationToken ct)
    {
        if (!HardCommitSha.IsWellFormed(commitSha))
        {
            return (null, $"'{commitSha}' is not a commit identifier");
        }

        var bare = _control.Layout.BareRepository(repositoryId);
        var path = SnapshotPath(purpose);

        // Removed first, so a directory an earlier attempt left behind cannot be measured as though
        // it were this one. The name is Hub derived, so this can never name anything else.
        await _worktrees.DisposeAsync(bare, path, ct);

        if (!await _worktrees.AddDetachedAsync(bare, path, commitSha, ct))
        {
            return (null, $"a tree at '{commitSha}' could not be created inside the environment");
        }

        return await ApplyOwnershipAsync(path, ct)
            ? (new HardPinnedSnapshot(path, commitSha, bare), null)
            : (null, $"the {purpose} tree could not be given to the account that runs the commands");
    }

    /// <summary>
    /// Gives the tree to the account that runs the commands in it.
    ///
    /// The same ownership model the agent's own worktree has, for the same reason and with the same
    /// care. Verification commands are repository-controlled work run as the agent, and they write:
    /// build output, caches and the machine readable report the Hub reads back. A tree the service
    /// user owned and the agent could only read would fail every step that produced a file, which is
    /// most of them - and it would fail as a broken test suite rather than as a permissions mistake.
    ///
    /// What stays with the service is the part that decides what the repository is. The <c>.git</c>
    /// pointer keeps the service as owner, and the sticky bit means the agent may remove what it
    /// created and nothing else - so a verification run cannot detach the tree from the commit it is
    /// supposed to be measuring.
    /// </summary>
    private async Task<bool> ApplyOwnershipAsync(string snapshot, CancellationToken ct)
    {
        var agent = _control.Profile.AgentUser.Trim();
        var service = _control.Profile.ServiceUser.Trim();

        var tree = await _control.OwnershipAsync([HardGitCommands.Chown, "-R", agent, snapshot], ct);

        if (!tree.Ok)
        {
            return false;
        }

        var pointer = await _control.OwnershipAsync(
            [HardGitCommands.Chown, service + ":" + agent, snapshot, snapshot + "/.git"], ct);

        if (!pointer.Ok)
        {
            return false;
        }

        var mode = await _control.OwnershipAsync([HardGitCommands.Chmod, "0640", snapshot + "/.git"], ct);

        if (!mode.Ok)
        {
            return false;
        }

        var sticky = await _control.OwnershipAsync([HardGitCommands.Chmod, "1770", snapshot], ct);

        return sticky.Ok;
    }

    /// <summary>
    /// Whether the tree is still at the commit it was created for.
    ///
    /// Asked before a run and again after it. Build output is expected and is why the tree is
    /// disposable; a command that moved HEAD has measured a different commit, and that result must
    /// never be attributed to this one.
    /// </summary>
    public async Task<(bool StillPinned, string Detail)> VerifyPinAsync(
        HardPinnedSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var head = HardWorkspaceControl.FirstLine(
            await _control.ServiceAsync(
                HardGitCommands.Hardened(
                    _control.Layout, "-C", snapshot.SnapshotPath, "rev-parse", "HEAD"), ct));

        if (head is not { Length: > 0 })
        {
            return (false, $"the tree at '{snapshot.SnapshotPath}' could not be read");
        }

        return string.Equals(head.Trim(), snapshot.CommitSha, StringComparison.OrdinalIgnoreCase)
            ? (true, "pinned")
            : (false, $"the tree is at '{head.Trim()}' rather than '{snapshot.CommitSha}'");
    }

    /// <summary>Takes the tree away, and says so when it will not go. Never forces.</summary>
    public Task<string?> ReleaseAsync(HardPinnedSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return _worktrees.DisposeAsync(snapshot.BareRepository, snapshot.SnapshotPath, ct);
    }

    /// <summary>
    /// Where a measurement tree lives, from a Hub composed identity and nothing else.
    ///
    /// Beneath the execution root, beside the review trees and apart from the agent's worktrees, so
    /// no repository name, task key or profile string ever reaches the filesystem.
    /// </summary>
    private string SnapshotPath(string purpose) =>
        _control.Layout.ExecutionRoot + "/verification/" + Sanitised(purpose) + "-"
        + HardWorkspaceLayout.Identity(Guid.CreateVersion7());

    private static string Sanitised(string purpose) =>
        new([.. purpose.Where(char.IsAsciiLetterOrDigit)]);
}
