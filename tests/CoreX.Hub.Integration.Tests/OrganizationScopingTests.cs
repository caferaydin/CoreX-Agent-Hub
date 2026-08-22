using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// ADR-0012: organization data must not be reachable across organizations through
/// ordinary application paths. This is logical isolation only; it is explicitly NOT an
/// OS security boundary, and these tests must never be read as proving one.
/// </summary>
public sealed class OrganizationScopingTests
{
    [Fact]
    public async Task Unscoped_session_sees_every_organization()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        await using var db = hub.NewContext();

        Assert.Equal(2, await db.Organizations.CountAsync());
        Assert.Equal(2, await db.Projects.CountAsync());
        Assert.Equal(2, await db.Repositories.CountAsync());
    }

    [Fact]
    public async Task Scoped_session_only_sees_its_own_organization()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        Guid gateOrganizationId;
        await using (var unscoped = hub.NewContext())
        {
            gateOrganizationId = (await unscoped.Organizations
                .SingleAsync(o => o.Name == "Hard Isolation Gate Fixture")).Id;
        }

        var scope = new OrganizationScope();
        scope.ScopeTo(gateOrganizationId);

        await using var scoped = hub.NewContext(scope);

        var organizations = await scoped.Organizations.ToListAsync();
        Assert.Single(organizations);
        Assert.Equal(gateOrganizationId, organizations[0].Id);

        Assert.All(await scoped.Projects.ToListAsync(), p => Assert.Equal(gateOrganizationId, p.OrganizationId));
        Assert.All(await scoped.Repositories.ToListAsync(), r => Assert.Equal(gateOrganizationId, r.OrganizationId));
        Assert.All(
            await scoped.VerificationProfiles.ToListAsync(),
            p => Assert.Equal(gateOrganizationId, p.OrganizationId));
    }

    [Fact]
    public async Task A_task_in_another_organization_is_invisible_to_a_scoped_session()
    {
        using var hub = TempHub.Create();

        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "scoping", "--allow", "src/**", "--human-criterion", "looks right");
        Assert.Equal(0, created.ExitCode);

        Guid otherOrganizationId;
        await using (var unscoped = hub.NewContext())
        {
            otherOrganizationId = (await unscoped.Organizations
                .SingleAsync(o => o.Name == "Hard Isolation Gate Fixture")).Id;

            Assert.Equal(1, await unscoped.Tasks.CountAsync());
        }

        var scope = new OrganizationScope();
        scope.ScopeTo(otherOrganizationId);

        await using var scoped = hub.NewContext(scope);
        Assert.Equal(0, await scoped.Tasks.CountAsync());
        Assert.Equal(0, await scoped.AcceptanceCriteria.CountAsync());
    }

    [Fact]
    public async Task Every_organization_owned_entity_type_carries_an_organization_column()
    {
        using var hub = TempHub.Create();
        await using var db = await hub.NewMigratedContextAsync();

        var missing = db.Model.GetEntityTypes()
            .Where(t => t.ClrType != typeof(Domain.Catalog.Organization))
            .Where(t => t.FindProperty("OrganizationId") is null)
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Every_entity_type_declares_a_query_filter()
    {
        using var hub = TempHub.Create();
        await using var db = await hub.NewMigratedContextAsync();

        var unfiltered = db.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public async Task System_audit_rows_stay_visible_under_any_scope()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        Guid organizationId;
        await using (var unscoped = hub.NewContext())
        {
            organizationId = (await unscoped.Organizations.FirstAsync()).Id;
        }

        var scope = new OrganizationScope();
        scope.ScopeTo(organizationId);

        await using var scoped = hub.NewContext(scope);
        var seedEvents = await scoped.AuditEvents.Where(a => a.EventType == "CatalogSeeded").ToListAsync();

        Assert.Single(seedEvents);
        Assert.Null(seedEvents[0].OrganizationId);
    }
}
