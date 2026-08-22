namespace CoreX.Hub.Verification.Profiles;

public sealed record ExecutableResolution(bool IsAllowed, string? AbsolutePath, string? Reason)
{
    public static ExecutableResolution Allowed(string absolutePath) => new(true, absolutePath, null);

    public static ExecutableResolution Rejected(string reason) => new(false, null, reason);
}

/// <summary>
/// Maps the logical executable name a verification step declares onto one absolute
/// executable path.
///
/// Fail closed by construction: a name that is not in the map resolves to nothing and the
/// process is never started. The search path is never consulted, and no external resolver
/// tool is run either - that would be one more process launch, and a repository
/// influenced one at that.
/// </summary>
public sealed class ExecutableAllowlist
{
    private readonly Dictionary<string, string> _entries;

    private ExecutableAllowlist(Dictionary<string, string> entries) => _entries = entries;

    public static ExecutableAllowlist Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<string> LogicalNames => _entries.Keys;

    /// <summary>
    /// Builds an allowlist, rejecting entries that could never be launched safely. An
    /// invalid entry is reported rather than dropped, because silently ignoring it would
    /// turn a configuration mistake into a confusing "unknown executable" later.
    /// </summary>
    public static ExecutableAllowlist Create(
        IEnumerable<KeyValuePair<string, string>> entries,
        out IReadOnlyList<string> problems)
    {
        var accepted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();

        foreach (var (name, path) in entries ?? [])
        {
            var logical = (name ?? string.Empty).Trim();
            var target = (path ?? string.Empty).Trim();

            if (logical.Length == 0)
            {
                rejected.Add("an executable mapping has an empty logical name");
                continue;
            }

            if (target.Length == 0)
            {
                rejected.Add($"executable '{logical}' maps to an empty path");
                continue;
            }

            if (!Path.IsPathRooted(target))
            {
                rejected.Add($"executable '{logical}' must map to an absolute path but maps to '{target}'");
                continue;
            }

            if (accepted.ContainsKey(logical))
            {
                rejected.Add($"executable '{logical}' is mapped more than once");
                continue;
            }

            // Stored exactly as configured. Canonicalisation belongs to FileSystemGuard
            // (E.1 #2), and this is a declared tool path rather than a managed workspace
            // path, so it is accepted as written or not at all.
            accepted[logical] = target;
        }

        problems = rejected;
        return new ExecutableAllowlist(accepted);
    }

    public ExecutableResolution Resolve(string? logicalName)
    {
        var name = (logicalName ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return ExecutableResolution.Rejected("the step declares no executable");
        }

        if (!_entries.TryGetValue(name, out var absolute))
        {
            var known = _entries.Count == 0
                ? "none are configured"
                : string.Join(", ", _entries.Keys.Order(StringComparer.OrdinalIgnoreCase));

            return ExecutableResolution.Rejected(
                $"'{name}' is not an allowlisted verification executable (allowed: {known})");
        }

        if (!File.Exists(absolute))
        {
            return ExecutableResolution.Rejected(
                $"allowlisted executable '{name}' does not exist at '{absolute}'");
        }

        return ExecutableResolution.Allowed(absolute);
    }
}
