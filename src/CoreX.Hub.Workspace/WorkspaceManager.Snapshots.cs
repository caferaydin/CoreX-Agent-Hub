namespace CoreX.Hub.Workspace;

public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Materialises a disposable working tree at one exact commit the caller already holds.
    ///
    /// This exists so a baseline can be measured against the commit an execution was
    /// actually pinned to, rather than against whatever the default branch points at by
    /// the time the measurement runs. Nothing here resolves a branch, a HEAD or a remote,
    /// and nothing contacts the network: the requested object is materialised or the call
    /// fails closed.
    ///
    /// The tree is detached on purpose. A baseline has no reason to own a branch ref, and
    /// creating one would pollute the repository's namespace with bookkeeping.
    ///
    /// Disposable is the point: verification will litter the tree with build output, and
    /// that is exactly why it must not run in the agent's worktree. The snapshot is thrown
    /// away rather than cleaned and reused.
    /// </summary>
    public async Task<PinnedSnapshotResult> PrepareSnapshotAsync(
        PinnedSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (NormalizeCommitSha(request.CommitSha) is not { } commitSha)
        {
            return SnapshotFailed(
                SnapshotIssueCode.CommitShaMalformed,
                $"'{request.CommitSha}' is not a full 40 character commit id; a branch name or a short "
                + "id would let the caller believe it pinned something it did not");
        }

        var repositoryDecision = _paths.Validate(request.RepositoryTrustRoot, request.RepositoryPath, mustExist: true);
        if (!repositoryDecision.IsAllowed)
        {
            return SnapshotFailed(SnapshotIssueCode.RepositoryUnusable, repositoryDecision.Reason ?? "path refused");
        }

        var repository = repositoryDecision.CanonicalPath!;

        var snapshotDecision = _paths.Validate(
            request.SnapshotRoot,
            Path.Combine(request.SnapshotRoot, request.RelativeSnapshotPath),
            mustExist: false);

        if (!snapshotDecision.IsAllowed)
        {
            return SnapshotFailed(SnapshotIssueCode.SnapshotPathRejected, snapshotDecision.Reason ?? "refused");
        }

        var snapshotPath = snapshotDecision.CanonicalPath!;

        if (Directory.Exists(snapshotPath) || File.Exists(snapshotPath))
        {
            return SnapshotFailed(SnapshotIssueCode.SnapshotPathAlreadyExists, snapshotPath);
        }

        // The object has to already exist locally. Nothing is fetched to make it exist.
        var exists = await GitAsync(
            repository,
            ["rev-parse", "--verify", "--quiet", commitSha + "^{commit}"],
            cancellationToken);

        if (!exists.Succeeded)
        {
            return SnapshotFailed(
                SnapshotIssueCode.CommitNotFound,
                $"commit {commitSha[..12]} does not exist in '{repository}'");
        }

        // Same repository scoped critical section as an ordinary preparation: worktree add
        // and the hardening that follows it both mutate shared repository metadata.
        var gate = RepositoryLocks.GetOrAdd(RepositoryLockKey(repository), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        // Set once the tree exists on disk. From that point a refusal is not just an answer
        // to the caller: it leaves a registered worktree behind that only this method knows
        // about, so it has to be taken back before returning.
        SnapshotIssueCode? refusedAfterAdd = null;
        var refusal = string.Empty;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);

            var add = await GitAsync(
                repository,
                ["worktree", "add", "--detach", snapshotPath, commitSha],
                cancellationToken);

            if (!add.Succeeded)
            {
                return SnapshotFailed(SnapshotIssueCode.WorktreeAddFailed, add.Describe());
            }

            var hardening = await HardenAsync(repository, snapshotPath, cancellationToken);
            if (hardening is not null)
            {
                refusedAfterAdd = SnapshotIssueCode.HardeningFailed;
                refusal = hardening;
            }
        }
        finally
        {
            gate.Release();
        }

        if (refusedAfterAdd is null)
        {
            // Trust git's answer rather than the request: if HEAD is not what was asked for,
            // the snapshot is not the thing the caller wanted and must not be usable as it.
            var observed = await ReadValueAsync(snapshotPath, ["rev-parse", "HEAD"], cancellationToken);

            if (!string.Equals(observed, commitSha, StringComparison.OrdinalIgnoreCase))
            {
                refusedAfterAdd = SnapshotIssueCode.HeadMismatch;
                refusal = $"the snapshot reports HEAD {observed ?? "(unknown)"} but {commitSha} was requested";
            }
        }

        if (refusedAfterAdd is { } code)
        {
            // A refused snapshot is never handed back, so nobody downstream can release it.
            // Leaving it would accumulate registered worktrees in the snapshot root that no
            // execution accounts for.
            return SnapshotFailed(code, refusal + await AbandonSnapshotAsync(repository, snapshotPath, commitSha));
        }

        return new PinnedSnapshotResult
        {
            Succeeded = true,
            Snapshot = new PinnedSnapshot
            {
                RepositoryPath = repository,
                SnapshotPath = snapshotPath,
                CommitSha = commitSha,
                ObservedHeadSha = commitSha,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
        };
    }

    /// <summary>
    /// Re-reads HEAD so a caller can refuse results a command produced after moving off the
    /// pinned commit.
    ///
    /// Generated build output is expected and is not drift: only HEAD identity matters
    /// here. A snapshot that moved is not a measurement of the commit it claims.
    /// </summary>
    public async Task<PinnedSnapshotCheck> VerifySnapshotPinAsync(
        PinnedSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var observed = await ReadValueAsync(snapshot.SnapshotPath, ["rev-parse", "HEAD"], cancellationToken);

        if (string.IsNullOrWhiteSpace(observed))
        {
            return new PinnedSnapshotCheck(false, null, "HEAD could not be read from the snapshot");
        }

        return string.Equals(observed, snapshot.CommitSha, StringComparison.OrdinalIgnoreCase)
            ? new PinnedSnapshotCheck(true, observed, null)
            : new PinnedSnapshotCheck(
                false,
                observed,
                $"the snapshot moved from {snapshot.CommitSha[..12]} to {observed[..Math.Min(12, observed.Length)]}");
    }

    /// <summary>
    /// Removes a disposable snapshot.
    ///
    /// The "a worktree without a result commit must be preserved" rule deliberately does
    /// not apply: that rule protects agent authored work a human might still need, and a
    /// snapshot contains none. A snapshot that will not go away is reported as orphaned,
    /// exactly like an execution worktree, rather than being forced out of the way.
    /// </summary>
    public Task<WorkspaceReleaseResult> ReleaseSnapshotAsync(
        PinnedSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Verification always leaves build output behind, so a snapshot is essentially
        // always "dirty" by the time it is finished with, and git refuses to remove a
        // dirty worktree. Discarding that output is the entire lifecycle of a snapshot:
        // it holds no agent authored work for anyone to recover, which is exactly why the
        // "preserve a worktree without a result commit" rule does not reach it.
        //
        // This is the single deliberate exception to E.1 #8 in the codebase, and it is
        // scoped to disposable snapshots: it removes one worktree the Hub created itself
        // and touches no ref, no branch and no history.
        return RemoveWorktreeAsync(
            snapshot.RepositoryPath,
            snapshot.SnapshotPath,
            ["worktree", "remove", "--force", snapshot.SnapshotPath],
            cancellationToken);
    }

    /// <summary>
    /// Takes back a snapshot that was materialised and then refused.
    ///
    /// The tree exists because this method created it, which makes removing it owed work
    /// rather than something the caller's token gets a say in - a cancelled preparation
    /// would otherwise leave the registration behind for good. The budget is the ordinary
    /// removal budget, so a wedged git still cannot hold the call open indefinitely.
    ///
    /// Returns a sentence to append to the refusal, and an empty string when the tree went
    /// away cleanly: a caller that never sees the snapshot has no other way to learn that
    /// one is still on disk.
    /// </summary>
    private async Task<string> AbandonSnapshotAsync(string repositoryPath, string snapshotPath, string commitSha)
    {
        using var undo = new CancellationTokenSource(
            (_options.GitTimeout + _options.RemovalRetryDelay) * _options.WorktreeRemovalAttempts);

        WorkspaceReleaseResult removal;

        try
        {
            // Through the ordinary disposal path, not a second removal of its own: a refused
            // snapshot is disposed of for exactly the reasons any other snapshot is, and the
            // one forcing removal in the codebase stays one.
            removal = await ReleaseSnapshotAsync(
                new PinnedSnapshot
                {
                    RepositoryPath = repositoryPath,
                    SnapshotPath = snapshotPath,
                    CommitSha = commitSha,
                    // Deliberately blank: the tree is being thrown away precisely because
                    // what it holds could not be established, and disposal reads neither
                    // this nor the commit id.
                    ObservedHeadSha = string.Empty,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                },
                undo.Token);
        }
        catch (OperationCanceledException)
        {
            return $"; the refused snapshot at '{snapshotPath}' could not be removed within the removal budget "
                   + "and is still registered";
        }

        return removal.State == WorkspaceReleaseState.Released
            ? string.Empty
            : $"; the refused snapshot at '{snapshotPath}' could not be removed and is still registered: "
              + removal.Detail;
    }

    private static PinnedSnapshotResult SnapshotFailed(SnapshotIssueCode code, string detail) => new()
    {
        Succeeded = false,
        Issues = [new SnapshotIssue(code, detail)],
    };

    /// <summary>
    /// A full object id, lowercased. A branch name or an abbreviated id is refused: the
    /// whole point of this seam is that the caller supplies the exact commit.
    /// </summary>
    private static string? NormalizeCommitSha(string? value)
    {
        var sha = (value ?? string.Empty).Trim();

        return sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha.ToLowerInvariant() : null;
    }
}
