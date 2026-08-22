using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// A managed local git repository. The Hub never writes to this working directory
/// directly; executions run in worktrees rooted outside it (ADR-0003, ADR-0004).
/// </summary>
public sealed class Repository
{
    private List<string> _protectedBranches = [];

    private Repository()
    {
        // EF Core
    }

    public Repository(
        Guid id,
        Guid organizationId,
        Guid projectId,
        Slug slug,
        string name,
        string localPath,
        string defaultBranch,
        IEnumerable<string> protectedBranches,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ProjectId = Guard.NotEmpty(projectId, nameof(projectId));
        Slug = Guard.NotNull(slug, nameof(slug));
        Name = Guard.NotBlankMax(name, nameof(name), 200);
        LocalPath = Guard.NotBlankMax(localPath, nameof(localPath), 400);
        DefaultBranch = Guard.NotBlankMax(defaultBranch, nameof(defaultBranch), 200);
        _protectedBranches = NormalizeBranches(protectedBranches, DefaultBranch);
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Slug Slug { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string LocalPath { get; private set; } = null!;

    public string DefaultBranch { get; private set; } = null!;

    /// <summary>
    /// Branches an execution may never target. The default branch is always protected.
    /// </summary>
    public IReadOnlyList<string> ProtectedBranches => _protectedBranches;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsProtectedBranch(string? branch) =>
        !string.IsNullOrWhiteSpace(branch)
        && _protectedBranches.Contains(branch.Trim(), StringComparer.Ordinal);

    public bool Update(
        string name,
        string localPath,
        string defaultBranch,
        IEnumerable<string> protectedBranches,
        DateTimeOffset nowUtc)
    {
        var newName = Guard.NotBlankMax(name, nameof(name), 200);
        var newPath = Guard.NotBlankMax(localPath, nameof(localPath), 400);
        var newDefaultBranch = Guard.NotBlankMax(defaultBranch, nameof(defaultBranch), 200);
        var newProtected = NormalizeBranches(protectedBranches, newDefaultBranch);

        var changed = Name != newName
            || LocalPath != newPath
            || DefaultBranch != newDefaultBranch
            || !_protectedBranches.SequenceEqual(newProtected);

        if (!changed)
        {
            return false;
        }

        Name = newName;
        LocalPath = newPath;
        DefaultBranch = newDefaultBranch;
        _protectedBranches = newProtected;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private static List<string> NormalizeBranches(IEnumerable<string> values, string defaultBranch) =>
        [.. (values ?? [])
            .Append(defaultBranch)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
