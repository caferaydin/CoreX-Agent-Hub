using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CoreX.Hub.Context.Abstractions;
using CoreX.Hub.Execution.FileSystem;

namespace CoreX.Hub.Composition.Context;

/// <summary>
/// Composition layer adapter that lets the context module reach FileSystemGuard without
/// referencing the execution module (E.1 #2 and E.1 #4 both hold).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileSystemGuardContextPathGuard : IContextPathGuard
{
    private readonly ConcurrentDictionary<string, FileSystemGuard?> _guards =
        new(StringComparer.OrdinalIgnoreCase);

    public ContextPathDecision Validate(string trustRoot, string candidate, bool mustExist)
    {
        var guard = _guards.GetOrAdd(trustRoot, root =>
        {
            FileSystemGuard.TryCreate(root, out var created);
            return created;
        });

        if (guard is null)
        {
            return ContextPathDecision.Rejected($"trust root '{trustRoot}' is not usable");
        }

        var result = mustExist ? guard.ValidateExisting(candidate) : guard.ValidateProposed(candidate);

        return result.IsAllowed
            ? ContextPathDecision.Allowed(result.CanonicalPath!)
            : ContextPathDecision.Rejected($"{result.Reason}: {result.Detail}");
    }
}
