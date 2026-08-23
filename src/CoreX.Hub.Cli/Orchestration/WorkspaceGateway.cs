using System.Runtime.Versioning;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Cli.Gates;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Composition layer adapter joining the orchestrator to the Phase 3 workspace module.
///
/// Every git command in an execution comes from here, which is what lets the orchestrator
/// sequence a pipeline without knowing that git exists.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceGateway : IExecutionWorkspace
{
    private readonly WorkspaceManager _manager;
    private readonly Func<Guid, RepositoryLocation> _locate;
    private readonly Dictionary<string, PreparedWorkspace> _prepared = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceGateway(WorkspaceManager manager, Func<Guid, RepositoryLocation> locate)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
    }

    public async Task<IExecutionWorkspacePreflight> PreflightAsync(Guid repositoryId, CancellationToken ct)
    {
        var location = _locate(repositoryId);

        var preflight = await _manager.PreflightAsync(
            location.RepositoryPath, location.RepositoryTrustRoot, location.DefaultBranch, ct);

        return new PinnedPreflight(location, preflight);
    }

    public async Task<PreparedExecutionWorkspace?> PrepareAsync(
        Guid repositoryId,
        IExecutionWorkspacePreflight preflight,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        // The workspace module's own answer is carried back to it unopened. Rebuilding one
        // here would mean resolving the branch a second time, which is exactly the drift
        // this handoff exists to make impossible.
        if (preflight is not PinnedPreflight pinned)
        {
            throw new InvalidOperationException(
                $"'{preflight.GetType().Name}' was not produced by this gateway, so the commit it names "
                + "cannot be shown to have come from this repository.");
        }

        var location = _locate(repositoryId);

        if (!string.Equals(pinned.Location.RepositoryPath, location.RepositoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the preflight was taken against '{pinned.Location.RepositoryPath}', not '{location.RepositoryPath}'.");
        }

        var result = await _manager.PrepareAsync(
            new WorkspaceRequest
            {
                RepositoryPath = location.RepositoryPath,
                RepositoryTrustRoot = location.RepositoryTrustRoot,
                WorkspaceRoot = location.WorkspaceRoot,
                OrganizationSlug = location.OrganizationSlug,
                RepositorySlug = location.RepositorySlug,
                TaskKey = taskKey,
                AttemptNo = attemptNo,
                DefaultBranch = location.DefaultBranch,
            },
            pinned.Result,
            ct);

        if (!result.Succeeded || result.Workspace is null)
        {
            return null;
        }

        _prepared[result.Workspace.WorkspacePath] = result.Workspace;

        return new PreparedExecutionWorkspace(
            result.Workspace.WorkspacePath,
            result.Workspace.BranchName,
            result.Workspace.BaseCommitSha);
    }

    public async Task<ExecutionCapture> CaptureAsync(PreparedExecutionWorkspace workspace, CancellationToken ct)
    {
        var capture = await _manager.CaptureChangesAsync(Resolve(workspace), ct);

        return new ExecutionCapture
        {
            BaseCommitSha = capture.BaseCommitSha,
            ObservedHeadSha = capture.CurrentHeadSha,
            UnexpectedCommitShas = capture.UnexpectedCommitShas,
            GitStatus = capture.GitStatus,
            GitCommitGraph = capture.GitCommitGraph,
            GitWorktreeDiff = capture.GitWorktreeDiff,
            FileInventory = string.Join(
                '\n',
                capture.FileInventory.Select(f => $"{f.ChangeStatus}\t{f.RelativePath}\t{f.SizeBytes}\t{f.Sha256}")),
            ChangedPaths = capture.ChangedPaths,

            // Raw added lines stay in memory for the secret gate. What reaches the evidence
            // store is redacted on the way in, so this is never the thing that gets stored.
            AddedLines = CapturedChangesMapper.ReadAddedLines(capture.GitWorktreeDiff),
        };
    }

    public async Task<ResultCommitOutcomeView> CreateResultCommitAsync(
        PreparedExecutionWorkspace workspace,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        var prepared = Resolve(workspace);
        var outcome = await _manager.CreateResultCommitAsync(prepared, taskKey, attemptNo, ct);

        return new ResultCommitOutcomeView(
            outcome.State == ResultCommitState.Created,
            outcome.CommitSha,
            outcome.CommitDiff,
            outcome.Detail ?? outcome.State.ToString(),
            outcome.UncommittedPaths,
            outcome.WorktreeStateKnown);
    }

    private PreparedWorkspace Resolve(PreparedExecutionWorkspace workspace) =>
        _prepared.TryGetValue(workspace.WorkspacePath, out var prepared)
            ? prepared
            : throw new InvalidOperationException(
                $"'{workspace.WorkspacePath}' was not prepared by this gateway.");

    /// <summary>
    /// The workspace module's own repository inspection, on its way back to it.
    ///
    /// The orchestrator sees a commit id and a sentence. The inspection that produced them
    /// never crosses into the Application layer and cannot be reconstructed there, which is
    /// what makes "prepare from the commit that was pinned" a fact about the code rather
    /// than a convention two halves are trusted to keep.
    /// </summary>
    private sealed class PinnedPreflight(RepositoryLocation location, RepositoryPreflightResult result)
        : IExecutionWorkspacePreflight
    {
        public RepositoryLocation Location { get; } = location;

        public RepositoryPreflightResult Result { get; } = result;

        /// <summary>
        /// Only a usable repository yields a commit. A repository with issues may still
        /// have resolved its default branch, and reporting that commit would let an
        /// execution be based on a repository the Hub already refused.
        /// </summary>
        public string? BaseCommitSha => Result.IsUsable ? Result.BaseCommitSha : null;

        public string Detail => Result.Describe();
    }
}

/// <summary>Where a repository lives and how the Hub is allowed to reach it.</summary>
public sealed record RepositoryLocation(
    string RepositoryPath,
    string RepositoryTrustRoot,
    string WorkspaceRoot,
    string OrganizationSlug,
    string RepositorySlug,
    string DefaultBranch);
