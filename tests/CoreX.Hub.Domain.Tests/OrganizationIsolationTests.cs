using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tests;

/// <summary>ADR-0017 / ADR-0012: the organization isolation invariant.</summary>
public sealed class OrganizationIsolationTests
{
    [Theory]
    [InlineData(IsolationLevel.Logical, false, OrganizationStatus.Active)]
    [InlineData(IsolationLevel.Hard, false, OrganizationStatus.Active)]
    [InlineData(IsolationLevel.Hard, true, OrganizationStatus.Active)]
    [InlineData(IsolationLevel.Logical, true, OrganizationStatus.Blocked)]
    public void Status_is_derived_from_the_isolation_invariant(
        IsolationLevel level,
        bool requiresHardIsolation,
        OrganizationStatus expected)
    {
        Assert.Equal(expected, Organization.ResolveStatus(level, requiresHardIsolation));

        var organization = DomainFixtures.Organization("hub-dev", level, requiresHardIsolation);
        Assert.Equal(expected, organization.Status);
    }

    [Fact]
    public void RequiresHardIsolation_with_logical_isolation_blocks_the_organization()
    {
        var organization = DomainFixtures.Organization(
            "isolation-gate-fixture", IsolationLevel.Logical, requiresHardIsolation: true);

        Assert.Equal(OrganizationStatus.Blocked, organization.Status);
        Assert.False(organization.CanStartExecution);
        Assert.NotNull(organization.BlockReason);
    }

    [Fact]
    public void Active_organization_can_start_an_execution()
    {
        var organization = DomainFixtures.Organization("hub-dev", IsolationLevel.Logical, requiresHardIsolation: false);

        Assert.Equal(OrganizationStatus.Active, organization.Status);
        Assert.True(organization.CanStartExecution);
        Assert.Null(organization.BlockReason);

        organization.EnsureCanStartExecution();
    }

    [Fact]
    public void Blocked_organization_refuses_to_start_an_execution()
    {
        var organization = DomainFixtures.Organization(
            "isolation-gate-fixture", IsolationLevel.Logical, requiresHardIsolation: true);

        var error = Assert.Throws<DomainException>(organization.EnsureCanStartExecution);
        Assert.Contains("blocked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Raising_isolation_to_hard_unblocks_the_organization()
    {
        var organization = DomainFixtures.Organization(
            "isolation-gate-fixture", IsolationLevel.Logical, requiresHardIsolation: true);

        Assert.Equal(OrganizationStatus.Blocked, organization.Status);

        var changed = organization.Update(
            organization.Name,
            organization.FilesystemRoot,
            IsolationLevel.Hard,
            requiresHardIsolation: true,
            organization.EnvironmentAllowlist,
            DomainFixtures.Now.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(OrganizationStatus.Active, organization.Status);
    }

    [Fact]
    public void Update_with_identical_values_reports_no_change()
    {
        var organization = DomainFixtures.Organization();

        var changed = organization.Update(
            organization.Name,
            organization.FilesystemRoot,
            organization.IsolationLevel,
            organization.RequiresHardIsolation,
            organization.EnvironmentAllowlist,
            DomainFixtures.Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(DomainFixtures.Now, organization.UpdatedAtUtc);
    }
}
