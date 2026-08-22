namespace CoreX.Hub.Workspace.Abstractions;

/// <summary>Outcome of a managed path trust decision, as seen by the workspace module.</summary>
public sealed record WorkspacePathDecision(bool IsAllowed, string? CanonicalPath, string? Reason)
{
    public static WorkspacePathDecision Allowed(string canonicalPath) => new(true, canonicalPath, null);

    public static WorkspacePathDecision Rejected(string reason) => new(false, null, reason);
}

/// <summary>
/// The Workspace owned port for managed filesystem path trust.
///
/// E.1 #2 says canonicalisation, root containment and reparse trust belong to
/// FileSystemGuard. E.1 #4 says this module may not reference the Execution module.
/// Both hold at once: the workspace asks through this port and the composition layer
/// answers with FileSystemGuard.
/// </summary>
public interface IWorkspacePathGuard
{
    WorkspacePathDecision Validate(string trustRoot, string candidate, bool mustExist);
}
