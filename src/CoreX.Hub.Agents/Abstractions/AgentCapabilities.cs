namespace CoreX.Hub.Agents.Abstractions;

/// <summary>
/// What a runtime can actually do (architecture F.1, ADR-0008).
///
/// Capabilities are facts, not aspirations. A runtime declares what it supports and the
/// Hub never emulates a missing one: a step that needs an unavailable capability is
/// reported Unsupported, which is a first class machine result rather than a failure to
/// paper over.
/// </summary>
public sealed record AgentCapabilities
{
    /// <summary>The runtime can be asked to produce output matching a supplied schema.</summary>
    public required bool SupportsStructuredSchema { get; init; }

    /// <summary>A previous session can be resumed by identifier.</summary>
    public required bool SupportsSessionResume { get; init; }

    /// <summary>The set of tools the agent may use can be restricted by the caller.</summary>
    public required bool SupportsToolAllowlist { get; init; }

    /// <summary>A spend ceiling can be given to the run.</summary>
    public required bool SupportsCostCap { get; init; }

    /// <summary>Output is observable incrementally while the run is in progress.</summary>
    public required bool SupportsStreaming { get; init; }

    /// <summary>The runtime can be constrained to make no changes at all.</summary>
    public required bool SupportsReadOnlyMode { get; init; }

    /// <summary>Nothing supported. A useful base for a runtime that declares its exceptions.</summary>
    public static AgentCapabilities None { get; } = new()
    {
        SupportsStructuredSchema = false,
        SupportsSessionResume = false,
        SupportsToolAllowlist = false,
        SupportsCostCap = false,
        SupportsStreaming = false,
        SupportsReadOnlyMode = false,
    };
}
