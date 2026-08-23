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

    public async Task<PreparedExecutionWorkspace?> PrepareAsync(
        Guid repositoryId,
        string taskKey,
        int attemptNo,
        CancellationToken ct)
    {
        var location = _locate(repositoryId);

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
            outcome.Detail ?? outcome.State.ToString());
    }

    private PreparedWorkspace Resolve(PreparedExecutionWorkspace workspace) =>
        _prepared.TryGetValue(workspace.WorkspacePath, out var prepared)
            ? prepared
            : throw new InvalidOperationException(
                $"'{workspace.WorkspacePath}' was not prepared by this gateway.");
}

/// <summary>Where a repository lives and how the Hub is allowed to reach it.</summary>
public sealed record RepositoryLocation(
    string RepositoryPath,
    string RepositoryTrustRoot,
    string WorkspaceRoot,
    string OrganizationSlug,
    string RepositorySlug,
    string DefaultBranch);
