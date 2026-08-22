namespace CoreX.Hub.Application.Abstractions;

/// <summary>
/// Ambient organization scope (architecture L.1).
///
/// When <see cref="OrganizationId"/> is set, persistence query filters restrict every
/// organization-owned record to that organization. When it is null the caller is
/// explicitly unscoped (for example <c>hub org list</c>).
///
/// This is logical isolation only. ADR-0012 accepts it for the Core X Logic pilot;
/// it is NOT an OS security boundary and must never be described as one.
/// </summary>
public sealed class OrganizationScope
{
    public Guid? OrganizationId { get; private set; }

    public bool IsScoped => OrganizationId is not null;

    public void ScopeTo(Guid organizationId) => OrganizationId = organizationId;

    public void Clear() => OrganizationId = null;
}
