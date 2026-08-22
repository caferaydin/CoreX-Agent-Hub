using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>Product grouping inside an organization. Owns the task-key prefix.</summary>
public sealed class Project
{
    private Project()
    {
        // EF Core
    }

    public Project(
        Guid id,
        Guid organizationId,
        Slug slug,
        string name,
        string taskKeyPrefix,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        Slug = Guard.NotNull(slug, nameof(slug));
        Name = Guard.NotBlankMax(name, nameof(name), 200);
        TaskKeyPrefix = NormalizePrefix(taskKeyPrefix);
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Slug Slug { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Prefix half of <see cref="TaskKey"/>. Valid prefixes are configuration, never a
    /// hard-coded list in code.
    /// </summary>
    public string TaskKeyPrefix { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool Update(string name, string taskKeyPrefix, DateTimeOffset nowUtc)
    {
        var newName = Guard.NotBlankMax(name, nameof(name), 200);
        var newPrefix = NormalizePrefix(taskKeyPrefix);

        if (Name == newName && TaskKeyPrefix == newPrefix)
        {
            return false;
        }

        Name = newName;
        TaskKeyPrefix = newPrefix;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private static string NormalizePrefix(string? prefix)
    {
        var value = Guard.NotBlankMax(prefix, "taskKeyPrefix", TaskKey.MaxPrefixLength);
        if (!TaskKey.IsValidPrefix(value))
        {
            throw new DomainException(
                $"'{value}' is not a valid task key prefix. Expected {TaskKey.MinPrefixLength}-{TaskKey.MaxPrefixLength} upper-case characters starting with a letter.");
        }

        return value;
    }
}
