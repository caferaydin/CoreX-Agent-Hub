using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// Ordered machine verification recipe for a repository. Steps are stored as a JSON
/// document rather than a table (architecture section M.2) because they are always
/// read and written as one unit.
/// </summary>
public sealed class VerificationProfile
{
    private List<VerificationStep> _steps = [];

    private VerificationProfile()
    {
        // EF Core
    }

    public VerificationProfile(
        Guid id,
        Guid organizationId,
        Guid repositoryId,
        string name,
        bool isDefault,
        string workingDirectory,
        IEnumerable<VerificationStep> steps,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        RepositoryId = Guard.NotEmpty(repositoryId, nameof(repositoryId));
        Name = Guard.NotBlankMax(name, nameof(name), 100);
        IsDefault = isDefault;
        WorkingDirectory = Guard.NotBlankMax(workingDirectory, nameof(workingDirectory), 400);
        _steps = NormalizeSteps(steps, Name);
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid RepositoryId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>At most one default profile per repository (enforced by a filtered index).</summary>
    public bool IsDefault { get; private set; }

    public string WorkingDirectory { get; private set; } = null!;

    public IReadOnlyList<VerificationStep> Steps => _steps;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<string> StepNames => [.. _steps.Select(s => s.Name)];

    /// <summary>
    /// Used by the ready gate to resolve a Machine acceptance criterion's
    /// <c>VerificationRef</c> (ADR-0015).
    /// </summary>
    public bool HasStep(string? stepName) =>
        !string.IsNullOrWhiteSpace(stepName)
        && _steps.Any(s => string.Equals(s.Name, stepName.Trim(), StringComparison.Ordinal));

    public bool Update(
        bool isDefault,
        string workingDirectory,
        IEnumerable<VerificationStep> steps,
        DateTimeOffset nowUtc)
    {
        var newWorkingDirectory = Guard.NotBlankMax(workingDirectory, nameof(workingDirectory), 400);
        var newSteps = NormalizeSteps(steps, Name);

        var changed = IsDefault != isDefault
            || WorkingDirectory != newWorkingDirectory
            || !_steps.SequenceEqual(newSteps);

        if (!changed)
        {
            return false;
        }

        IsDefault = isDefault;
        WorkingDirectory = newWorkingDirectory;
        _steps = newSteps;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private static List<VerificationStep> NormalizeSteps(IEnumerable<VerificationStep> steps, string profileName)
    {
        var list = (steps ?? []).ToList();
        if (list.Count == 0)
        {
            throw new DomainException($"Verification profile '{profileName}' must declare at least one step.");
        }

        var duplicate = list
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new DomainException(
                $"Verification profile '{profileName}' declares step '{duplicate.Key}' more than once.");
        }

        return list;
    }
}
