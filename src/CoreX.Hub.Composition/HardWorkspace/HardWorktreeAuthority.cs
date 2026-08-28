namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// The one place on the hard side that builds a git worktree command.
///
/// There were briefly two: the execution workspace made a branched tree for the agent, and a review
/// snapshot made a detached one at the result commit. Two implementations of the same operation is
/// how the rules drift apart - one of them reads HEAD back and the other trusts the exit code, one
/// runs through the hardened git configuration and the other is written in a hurry - so an
/// architecture guard exists to keep it at one, and it was right to object.
///
/// Everything here goes through <see cref="HardGitCommands.Hardened"/>, which is what suppresses
/// the repository's own hooks path and global configuration. A checkout is a place repository
/// controlled code gets to run, and the hardening is the reason it does not.
///
/// The commit is always a resolved object name and never a branch, a tag or a revision expression.
/// Those can move between being written down and being checked out, and a tree at a different
/// commit than the one an execution was measured against is worse than no tree.
///
/// Nothing here repairs anything. There is no <c>--force</c>, no reset and no ref update: a tree is
/// created, or it is refused; it is removed by deleting a directory this authority created and
/// pruning the administrative record. Removing a worktree is disposal, and it must never become a
/// way to make an agent's unexpected commit stop existing.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class HardWorktreeAuthority
{
    private const string Remove = "/usr/bin/rm";
    private const string Describe = "/usr/bin/stat";

    private readonly HardWorkspaceControl _control;

    public HardWorktreeAuthority(HardWorkspaceControl control) =>
        _control = control ?? throw new ArgumentNullException(nameof(control));

    /// <summary>
    /// A tree on a new branch, for an agent to work in.
    ///
    /// The branch is the Hub's own name for this attempt. HEAD is read back and compared, because
    /// a worktree that came up somewhere else is not the workspace the execution was pinned to.
    /// </summary>
    public async Task<bool> AddBranchedAsync(
        string bareRepository, string worktree, string branch, string commitSha, CancellationToken ct)
    {
        if (!IsResolvedCommit(commitSha))
        {
            return false;
        }

        var added = await _control.GitAsync(
            ct, "--git-dir=" + bareRepository, "worktree", "add", "-b", branch, worktree, commitSha);

        return added.Ok && await HeadIsAsync(worktree, commitSha, ct);
    }

    /// <summary>
    /// A detached tree at exactly one commit, for something to be examined at.
    ///
    /// Detached on purpose: nothing that runs in this tree should be able to move a branch, and a
    /// reviewer that committed would be a reviewer that changed what it was measuring.
    /// </summary>
    public async Task<bool> AddDetachedAsync(
        string bareRepository, string worktree, string commitSha, CancellationToken ct)
    {
        if (!IsResolvedCommit(commitSha))
        {
            return false;
        }

        var added = await _control.GitAsync(
            ct, "--git-dir=" + bareRepository, "worktree", "add", "--detach", worktree, commitSha);

        return added.Ok && await HeadIsAsync(worktree, commitSha, ct);
    }

    /// <summary>
    /// Asks git to give a tree back, and reports rather than insisting.
    ///
    /// The agent's worktree is released this way. A tree that will not come away cleanly is
    /// something an operator should look at: "remove it harder" is how evidence disappears, so
    /// there is no force here and the refusal is returned as it came.
    /// </summary>
    public async Task<(bool Ok, string Detail)> RemoveAsync(
        string bareRepository, string worktree, CancellationToken ct)
    {
        var removed = await _control.GitAsync(
            ct, "--git-dir=" + bareRepository, "worktree", "remove", worktree);

        return (removed.Ok, removed.Ok ? "released" : removed.Stderr.Trim());
    }

    /// <summary>Whether a tree is still on disk.</summary>
    public async Task<bool> ExistsAsync(string worktree, CancellationToken ct) =>
        (await _control.ServiceAsync([Describe, "-c", "%F", worktree], ct)).Ok;

    /// <summary>
    /// Disposes a tree this authority created, without force and without repair.
    ///
    /// The directory is removed and the administrative record pruned. <c>worktree remove
    /// --force</c> would do both in one command and is deliberately not used: it is the same
    /// <c>--force</c> the frozen guards forbid anywhere near this module, and a disposal path that
    /// carries it is one edit away from becoming a repair path.
    ///
    /// The caller must already have established that the path is Hub owned and contained. This
    /// removes what it is given and has no way to decide whether it should have been given it,
    /// which is why nothing reachable from a task, a provider or repository content may call it.
    /// </summary>
    public async Task<string?> DisposeAsync(
        string bareRepository, string worktree, CancellationToken ct)
    {
        await _control.ServiceAsync([Remove, "-rf", worktree], ct);

        // Prune reads the trees it already knows about and forgets the ones whose directories have
        // gone. It cannot touch a commit, a ref or a tree anybody is still using.
        await _control.GitAsync(ct, "--git-dir=" + bareRepository, "worktree", "prune");

        return await ExistsAsync(worktree, ct)
            ? $"the worktree at '{worktree}' could not be removed"
            : null;
    }

    private async Task<bool> HeadIsAsync(string worktree, string commitSha, CancellationToken ct)
    {
        var head = HardWorkspaceControl.FirstLine(
            await _control.GitAsync(ct, "-C", worktree, "rev-parse", "HEAD"));

        return string.Equals(head, commitSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether this is an object name rather than something that could resolve elsewhere.
    ///
    /// A branch, a tag, <c>HEAD~1</c> or anything beginning with a dash is refused before it
    /// reaches an argument vector. The contract says commit, so only a commit is accepted.
    /// </summary>
    private static bool IsResolvedCommit(string? commitSha) =>
        commitSha is { Length: >= 7 and <= 64 } && commitSha.All(char.IsAsciiHexDigit);
}
