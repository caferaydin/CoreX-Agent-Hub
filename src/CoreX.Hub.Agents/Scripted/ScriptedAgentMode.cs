namespace CoreX.Hub.Agents.Scripted;

/// <summary>
/// The deterministic scenarios the scripted runtime can produce (architecture F.2).
///
/// Each mode creates a *condition*. None of them decides what that condition means: a
/// scope violation, a blocked secret or a runtime policy violation is a gate verdict, and
/// the gates do not exist yet.
/// </summary>
public enum ScriptedAgentMode
{
    /// <summary>Succeeds and changes nothing.</summary>
    NoOp,

    /// <summary>Applies a fixed change and succeeds.</summary>
    ApplyPatch,

    /// <summary>Writes inside the workspace but outside a plausible allowed scope.</summary>
    OutOfScopeWrite,

    /// <summary>Writes synthetic secret bearing content inside the workspace.</summary>
    SecretWrite,

    /// <summary>Writes part of its work and then fails.</summary>
    PartialWrite,

    /// <summary>Stays alive past its deadline so timeout and cancellation can be exercised.</summary>
    Hang,

    /// <summary>Dies rather than exiting, so a crash is distinguishable from a failure.</summary>
    Crash,

    /// <summary>Commits in the workspace, which the Hub never asked it to do.</summary>
    UnexpectedCommit,
}

/// <summary>
/// How one scripted runtime instance is configured. The scenario is a property of the
/// runtime, not of the shared request: adding a mode field to AgentRunRequest would put a
/// test concern into the contract every real provider has to implement.
/// </summary>
public sealed record ScriptedAgentOptions
{
    /// <summary>
    /// Absolute path to the deterministic fixture executable this runtime drives. It is
    /// supplied, never discovered: nothing here searches PATH.
    /// </summary>
    public required string FixtureExecutablePath { get; init; }

    public required ScriptedAgentMode Mode { get; init; }
}
