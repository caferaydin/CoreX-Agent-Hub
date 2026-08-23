namespace CoreX.Hub.Context.Abstractions;

/// <summary>Outcome of a managed path trust decision, as seen by the context module.</summary>
public sealed record ContextPathDecision(bool IsAllowed, string? CanonicalPath, string? Reason)
{
    public static ContextPathDecision Allowed(string canonicalPath) => new(true, canonicalPath, null);

    public static ContextPathDecision Rejected(string reason) => new(false, null, reason);
}

/// <summary>
/// The Context owned port for managed path trust.
///
/// E.1 #2 puts canonicalisation, root containment and reparse trust in FileSystemGuard;
/// E.1 #4 forbids this module from referencing the execution module. Both hold: the
/// builder asks through this port and the composition layer answers with FileSystemGuard.
/// </summary>
public interface IContextPathGuard
{
    ContextPathDecision Validate(string trustRoot, string candidate, bool mustExist);
}
