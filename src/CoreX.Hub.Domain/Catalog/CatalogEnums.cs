namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// How strongly an organization is separated from other organizations at runtime.
/// ADR-0012 accepts <see cref="Logical"/> for the Core X Logic pilot only.
/// ADR-0017 requires <see cref="Hard"/> before an organization that demands it may run.
/// </summary>
public enum IsolationLevel
{
    /// <summary>In-process scoping only. Prevents accidents, not attacks.</summary>
    Logical = 0,

    /// <summary>OS-level separation (separate principal / WSL2 distribution / container).</summary>
    Hard = 1,
}

/// <summary>Whether the organization may take part in executions.</summary>
public enum OrganizationStatus
{
    Active = 0,

    /// <summary>
    /// Set automatically when <c>RequiresHardIsolation</c> is true while the effective
    /// isolation level is <see cref="IsolationLevel.Logical"/> (ADR-0017).
    /// </summary>
    Blocked = 1,
}

/// <summary>Role an agent profile plays inside an execution pipeline.</summary>
public enum AgentRole
{
    Implementer = 0,
    Reviewer = 1,
}

/// <summary>
/// Coarse permission envelope requested from an agent runtime.
/// The runtime declares whether it can actually enforce it (ADR-0008).
/// </summary>
public enum AgentPermissionProfile
{
    ReadOnly = 0,
    WorkspaceWrite = 1,
}

/// <summary>How a verification step's output is turned into structured evidence.</summary>
public enum VerificationResultParser
{
    None = 0,
    Trx = 1,
    JUnit = 2,
}
