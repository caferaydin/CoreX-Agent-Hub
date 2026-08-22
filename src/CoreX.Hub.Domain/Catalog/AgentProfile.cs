using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Catalog;

/// <summary>
/// Binds a role to a provider/runtime and its execution envelope.
/// ADR-0008: roles are never hard-bound to a provider; swapping the provider for a role
/// must be a configuration change, not a code change.
/// </summary>
public sealed class AgentProfile
{
    private AgentProfile()
    {
        // EF Core
    }

    public AgentProfile(
        Guid id,
        Guid organizationId,
        string name,
        AgentRole role,
        string provider,
        string runtimeKey,
        AgentPermissionProfile permissionProfile,
        string? model,
        string? effort,
        int softTimeoutSeconds,
        int idleTimeoutSeconds,
        int hardTimeoutSeconds,
        DateTimeOffset nowUtc)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        Name = Guard.NotBlankMax(name, nameof(name), 100);
        Role = role;
        Provider = Guard.NotBlankMax(provider, nameof(provider), 100);
        RuntimeKey = Guard.NotBlankMax(runtimeKey, nameof(runtimeKey), 100);
        PermissionProfile = permissionProfile;
        Model = Guard.OptionalMax(model, nameof(model), 100);
        Effort = Guard.OptionalMax(effort, nameof(effort), 20);
        SoftTimeoutSeconds = Guard.Positive(softTimeoutSeconds, nameof(softTimeoutSeconds));
        IdleTimeoutSeconds = Guard.Positive(idleTimeoutSeconds, nameof(idleTimeoutSeconds));
        HardTimeoutSeconds = Guard.Positive(hardTimeoutSeconds, nameof(hardTimeoutSeconds));
        EnsureTimeoutOrder();
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    public AgentRole Role { get; private set; }

    /// <summary>Provider family, for example <c>Scripted</c>, <c>ClaudeCode</c>, <c>Codex</c>.</summary>
    public string Provider { get; private set; } = null!;

    /// <summary>Key an <c>IAgentRuntime</c> implementation registers under (Phase 5+).</summary>
    public string RuntimeKey { get; private set; } = null!;

    public AgentPermissionProfile PermissionProfile { get; private set; }

    public string? Model { get; private set; }

    public string? Effort { get; private set; }

    public int SoftTimeoutSeconds { get; private set; }

    public int IdleTimeoutSeconds { get; private set; }

    public int HardTimeoutSeconds { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool Update(
        AgentRole role,
        string provider,
        string runtimeKey,
        AgentPermissionProfile permissionProfile,
        string? model,
        string? effort,
        int softTimeoutSeconds,
        int idleTimeoutSeconds,
        int hardTimeoutSeconds,
        DateTimeOffset nowUtc)
    {
        var newProvider = Guard.NotBlankMax(provider, nameof(provider), 100);
        var newRuntimeKey = Guard.NotBlankMax(runtimeKey, nameof(runtimeKey), 100);
        var newModel = Guard.OptionalMax(model, nameof(model), 100);
        var newEffort = Guard.OptionalMax(effort, nameof(effort), 20);

        var changed = Role != role
            || Provider != newProvider
            || RuntimeKey != newRuntimeKey
            || PermissionProfile != permissionProfile
            || Model != newModel
            || Effort != newEffort
            || SoftTimeoutSeconds != softTimeoutSeconds
            || IdleTimeoutSeconds != idleTimeoutSeconds
            || HardTimeoutSeconds != hardTimeoutSeconds;

        if (!changed)
        {
            return false;
        }

        Role = role;
        Provider = newProvider;
        RuntimeKey = newRuntimeKey;
        PermissionProfile = permissionProfile;
        Model = newModel;
        Effort = newEffort;
        SoftTimeoutSeconds = Guard.Positive(softTimeoutSeconds, nameof(softTimeoutSeconds));
        IdleTimeoutSeconds = Guard.Positive(idleTimeoutSeconds, nameof(idleTimeoutSeconds));
        HardTimeoutSeconds = Guard.Positive(hardTimeoutSeconds, nameof(hardTimeoutSeconds));
        EnsureTimeoutOrder();
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private void EnsureTimeoutOrder()
    {
        if (SoftTimeoutSeconds > IdleTimeoutSeconds || IdleTimeoutSeconds > HardTimeoutSeconds)
        {
            throw new DomainException(
                $"Agent profile '{Name}' timeouts must satisfy soft <= idle <= hard (architecture F.5).");
        }
    }
}
