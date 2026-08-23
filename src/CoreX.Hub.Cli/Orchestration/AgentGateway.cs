using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Composition layer adapter that resolves a runtime and runs it.
///
/// Resolution is data driven: the profile names a runtime key and the registry answers
/// with whatever was registered under it. There is no branch on a provider here and none
/// upstream either - the orchestrator never learns which runtime it got, which is what
/// makes a local harness and a hosted assistant interchangeable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentGateway : IAgentRuntimeGateway
{
    private readonly IReadOnlyDictionary<string, IAgentRuntime> _runtimes;

    public AgentGateway(IReadOnlyDictionary<string, IAgentRuntime> runtimes) =>
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));

    public async Task<AgentExecutionResult> RunAsync(
        AgentProfile profile,
        PreparedExecutionWorkspace workspace,
        BuiltContextPack context,
        HubTask task,
        string correlationId,
        CancellationToken ct)
    {
        if (!_runtimes.TryGetValue(profile.RuntimeKey, out var runtime))
        {
            var known = _runtimes.Count == 0 ? "none are registered" : string.Join(", ", _runtimes.Keys.Order());

            return new AgentExecutionResult(
                nameof(AgentRunOutcome.Failed), null, string.Empty, string.Empty, null, TimeSpan.Zero,
                $"no runtime is registered under '{profile.RuntimeKey}' (registered: {known})");
        }

        var request = new AgentRunRequest
        {
            WorkingDirectory = workspace.WorkspacePath,
            SystemPromptFile = context.SystemPromptPath,
            UserPrompt = task.Objective,
            PermissionProfile = profile.PermissionProfile,
            ModelHint = profile.Model,
            EffortHint = profile.Effort,
            EnvironmentAllowlist = ["SystemRoot", "TEMP", "TMP", "PATH"],
            SoftTimeout = TimeSpan.FromSeconds(profile.SoftTimeoutSeconds),
            IdleTimeout = TimeSpan.FromSeconds(profile.IdleTimeoutSeconds),
            HardTimeout = TimeSpan.FromSeconds(profile.HardTimeoutSeconds),
            CorrelationId = correlationId,
        };

        var result = await runtime.RunAsync(request, new Progress<AgentEvent>(_ => { }), ct);

        return new AgentExecutionResult(
            result.Outcome.ToString(),
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StructuredOutput,
            result.Duration,
            result.Diagnostic);
    }
}
