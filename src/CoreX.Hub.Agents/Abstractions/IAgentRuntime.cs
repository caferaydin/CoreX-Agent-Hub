namespace CoreX.Hub.Agents.Abstractions;

/// <summary>
/// The one abstraction every agent runtime implements (architecture F.1, ADR-0008).
///
/// Exactly three members, and it stays that way. Authentication, model selection, session
/// creation, quota, billing and account management are not runtime contract concerns:
/// they belong to the composition that chose this runtime, or to the provider's own tool.
/// Adding a member here to accommodate one provider would make every other provider
/// implement a lie.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>What this runtime can actually do. Facts, never aspirations.</summary>
    AgentCapabilities Describe();

    /// <summary>Runs one agent turn and reports machine truth about it.</summary>
    Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        IProgress<AgentEvent> sink,
        CancellationToken cancellationToken);

    /// <summary>Whether this runtime is usable right now.</summary>
    Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken);
}
