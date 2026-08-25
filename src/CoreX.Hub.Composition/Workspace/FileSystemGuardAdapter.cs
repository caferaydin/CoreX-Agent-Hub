using System.Collections.Concurrent;
using System.Runtime.Versioning;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Workspace.Abstractions;

namespace CoreX.Hub.Composition.Workspace;

/// <summary>
/// Composition layer adapter that lets the workspace module reach FileSystemGuard without
/// referencing the execution module (E.1 #2 and E.1 #4 both hold).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileSystemGuardAdapter : IWorkspacePathGuard
{
    private readonly ConcurrentDictionary<string, FileSystemGuard?> _guards =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspacePathDecision Validate(string trustRoot, string candidate, bool mustExist)
    {
        var guard = _guards.GetOrAdd(trustRoot, root =>
        {
            FileSystemGuard.TryCreate(root, out var created);
            return created;
        });

        if (guard is null)
        {
            return WorkspacePathDecision.Rejected($"trust root '{trustRoot}' is not usable");
        }

        var result = mustExist ? guard.ValidateExisting(candidate) : guard.ValidateProposed(candidate);

        return result.IsAllowed
            ? WorkspacePathDecision.Allowed(result.CanonicalPath!)
            : WorkspacePathDecision.Rejected($"{result.Reason}: {result.Detail}");
    }
}
