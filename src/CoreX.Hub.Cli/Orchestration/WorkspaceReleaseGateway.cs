using System.Runtime.Versioning;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Workspace;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Composition layer adapter joining a recorded decision to the workspace module.
///
/// It translates and nothing else: acceptance says which execution may be released, and the
/// workspace module decides whether and how, exactly as it does for the operator command.
/// In particular the automatic path never confirms on an operator's behalf - it passes
/// <c>operatorConfirmed: false</c>, so a commitless worktree is refused by the same D6 check
/// that guards <c>hub workspace release</c> rather than by the caller remembering to ask.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceReleaseGateway : IExecutionWorkspaceRelease
{
    private readonly WorkspaceManager _manager;
    private readonly Func<Guid, RepositoryLocation> _locate;

    public WorkspaceReleaseGateway(WorkspaceManager manager, Func<Guid, RepositoryLocation> locate)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
    }

    public async Task<WorkspaceCleanupResult> ReleaseAsync(
        Guid repositoryId,
        DomainExecution execution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var location = _locate(repositoryId);

        var workspace = new PreparedWorkspace
        {
            RepositoryPath = location.RepositoryPath,
            WorkspacePath = execution.WorktreePath,
            BranchName = execution.BranchName,
            BaseCommitSha = execution.BaseCommitSha,
            HooksDirectory = _manager.Hooks.HooksDirectory,
            CreatedAtUtc = execution.CreatedAtUtc,
        };

        var result = await _manager.ReleaseAsync(
            workspace,
            hasResultCommit: execution.ResultCommitSha is not null,
            operatorConfirmed: false,
            ct);

        return new WorkspaceCleanupResult(OutcomeOf(result.State), Describe(result, execution));
    }

    /// <summary>
    /// Exhaustive on purpose. A release state this does not know is not something to report
    /// as a success or a failure by default - it means the workspace vocabulary moved and
    /// nobody said what it now means for a decision.
    /// </summary>
    private static WorkspaceCleanupOutcome OutcomeOf(WorkspaceReleaseState state) => state switch
    {
        WorkspaceReleaseState.Released => WorkspaceCleanupOutcome.Released,
        WorkspaceReleaseState.Orphaned => WorkspaceCleanupOutcome.Orphaned,
        WorkspaceReleaseState.DeniedNoResultCommit => WorkspaceCleanupOutcome.DeniedNoResultCommit,
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state, "Unknown workspace release state: a decision would report the wrong thing."),
    };

    private static string Describe(WorkspaceReleaseResult result, DomainExecution execution) =>
        result.State switch
        {
            WorkspaceReleaseState.Released =>
                $"the worktree at '{execution.WorktreePath}' was removed; branch "
                + $"'{execution.BranchName}' and the result commit remain",

            _ => result.Detail ?? result.State.ToString(),
        };
}
