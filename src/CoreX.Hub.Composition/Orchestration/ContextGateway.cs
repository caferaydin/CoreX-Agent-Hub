using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Composition layer adapter joining the orchestrator to the Phase 5A context builder.
///
/// It translates already accepted task truth into the builder's request. It does not
/// decide what is true and it adds nothing of its own: the pack is a serialisation of the
/// task, not an interpretation of it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContextGateway : IExecutionContextBuilder
{
    private readonly ContextPackBuilder _builder;
    private readonly string _contextRoot;
    private readonly Func<Guid, ContextRepositoryFacts> _facts;

    public ContextGateway(ContextPackBuilder builder, string contextRoot, Func<Guid, ContextRepositoryFacts> facts)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _contextRoot = contextRoot ?? throw new ArgumentNullException(nameof(contextRoot));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
    }

    public async Task<BuiltContextPack?> BuildAsync(
        Guid executionId,
        Guid repositoryId,
        HubTask task,
        int attemptNo,
        string baseCommitSha,
        CancellationToken ct)
    {
        var facts = _facts(repositoryId);

        var result = await _builder.BuildAsync(
            new ContextBuildRequest
            {
                ExecutionId = executionId,
                ContextRoot = _contextRoot,
                TaskKey = task.TaskKey.Value,
                Objective = task.Objective,
                AllowedScope = task.AllowedScope,
                ForbiddenScope = task.ForbiddenScope,
                AttemptNo = attemptNo,
                AcceptanceCriteria =
                [
                    .. task.AcceptanceCriteria.Select(c => new ContextAcceptanceCriterion
                    {
                        Order = c.Order,
                        Text = c.Text,
                        Kind = c.Kind,
                        VerificationRef = c.VerificationRef,
                        HumanCheckHint = c.HumanCheckHint,
                    }),
                ],
                RepositoryState = new ContextRepositoryState
                {
                    RepositoryId = repositoryId,

                    // The execution's own pinned commit, not whatever the branch says now.
                    BaseCommitSha = baseCommitSha,
                    DefaultBranch = facts.DefaultBranch,
                    ProtectedBranches = facts.ProtectedBranches,
                },
            },
            ct);

        return result.Succeeded
            ? new BuiltContextPack(
                result.Pack!.PackPath,
                result.Pack.ContextPackHash,
                Path.Combine(result.Pack.PackPath, ContextPackBuilder.SystemFileName),
                Path.Combine(result.Pack.PackPath, ContextPackBuilder.RulesFileName))
            : null;
    }
}

/// <summary>The repository facts a context pack is allowed to state.</summary>
public sealed record ContextRepositoryFacts(string DefaultBranch, IReadOnlyList<string> ProtectedBranches);
