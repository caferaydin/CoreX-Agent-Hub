using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// Isolation root. Every other record in the system carries this organization's id
/// (architecture section L.1).
/// </summary>
public sealed class Organization
{
    private List<string> _environmentAllowlist = [];

    private Organization()
    {
        // EF Core
    }

    public Organization(
        Guid id,
        Slug slug,
        string name,
        string filesystemRoot,
        IsolationLevel isolationLevel,
        bool requiresHardIsolation,
        IEnumerable<string> environmentAllowlist,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        Slug = Guard.NotNull(slug, nameof(slug));
        Name = Guard.NotBlankMax(name, nameof(name), 200);
        FilesystemRoot = Guard.NotBlankMax(filesystemRoot, nameof(filesystemRoot), 400);
        IsolationLevel = isolationLevel;
        RequiresHardIsolation = requiresHardIsolation;
        _environmentAllowlist = NormalizeAllowlist(environmentAllowlist);
        Status = ResolveStatus(isolationLevel, requiresHardIsolation);
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Slug Slug { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>Organization-scoped filesystem root (ADR-0004 / architecture L.1).</summary>
    public string FilesystemRoot { get; private set; } = null!;

    public IsolationLevel IsolationLevel { get; private set; }

    public bool RequiresHardIsolation { get; private set; }

    /// <summary>Derived from the isolation invariant; never set directly by callers.</summary>
    public OrganizationStatus Status { get; private set; }

    /// <summary>
    /// ADR-0007: agent and verification processes start from an empty environment and
    /// receive only these variables.
    /// </summary>
    public IReadOnlyList<string> EnvironmentAllowlist => _environmentAllowlist;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>ADR-0017: a blocked organization may never start an execution.</summary>
    public bool CanStartExecution => Status == OrganizationStatus.Active;

    public string? BlockReason => Status == OrganizationStatus.Blocked
        ? "Organization requires hard OS-level isolation but is configured as Logical (ADR-0017)."
        : null;

    public void EnsureCanStartExecution()
    {
        if (!CanStartExecution)
        {
            throw new DomainException(
                $"Organization '{Slug}' is blocked and cannot start an execution. {BlockReason}");
        }
    }

    public bool Update(
        string name,
        string filesystemRoot,
        IsolationLevel isolationLevel,
        bool requiresHardIsolation,
        IEnumerable<string> environmentAllowlist,
        DateTimeOffset nowUtc)
    {
        var newName = Guard.NotBlankMax(name, nameof(name), 200);
        var newRoot = Guard.NotBlankMax(filesystemRoot, nameof(filesystemRoot), 400);
        var newAllowlist = NormalizeAllowlist(environmentAllowlist);

        var changed = Name != newName
            || FilesystemRoot != newRoot
            || IsolationLevel != isolationLevel
            || RequiresHardIsolation != requiresHardIsolation
            || !_environmentAllowlist.SequenceEqual(newAllowlist);

        if (!changed)
        {
            return false;
        }

        Name = newName;
        FilesystemRoot = newRoot;
        IsolationLevel = isolationLevel;
        RequiresHardIsolation = requiresHardIsolation;
        _environmentAllowlist = newAllowlist;
        Status = ResolveStatus(isolationLevel, requiresHardIsolation);
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// ADR-0017 invariant, expressed as a pure function so it can be tested without an
    /// instance and can never be bypassed by a setter.
    /// </summary>
    public static OrganizationStatus ResolveStatus(IsolationLevel isolationLevel, bool requiresHardIsolation) =>
        requiresHardIsolation && isolationLevel == IsolationLevel.Logical
            ? OrganizationStatus.Blocked
            : OrganizationStatus.Active;

    private static List<string> NormalizeAllowlist(IEnumerable<string> values) =>
        [.. (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
