using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Architecture section 21: the catalog seed is deterministic, idempotent and never
/// deletes persisted records.
/// </summary>
public sealed class SeedTests
{
    [Fact]
    public async Task First_seed_creates_the_configured_catalog()
    {
        using var hub = TempHub.Create();
        var report = await PersistenceTests.SeedAsync(hub);

        Assert.Equal(2, Count(report, "Organization").Created);
        Assert.Equal(2, Count(report, "Project").Created);
        Assert.Equal(2, Count(report, "Repository").Created);
        Assert.Equal(2, Count(report, "VerificationProfile").Created);
        Assert.Equal(1, Count(report, "AgentProfile").Created);
        Assert.Empty(report.Orphans);
        Assert.False(report.MadeNoChanges);
    }

    [Fact]
    public async Task Second_seed_changes_nothing()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        var second = await PersistenceTests.SeedAsync(hub);

        Assert.True(second.MadeNoChanges);
        Assert.Equal(0, second.TotalCreated);
        Assert.Equal(0, second.TotalUpdated);
        Assert.Equal(9, second.TotalUnchanged);
    }

    [Fact]
    public async Task Repeated_seeding_does_not_grow_the_database()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        var firstSnapshot = await SnapshotAsync(hub);

        await PersistenceTests.SeedAsync(hub);
        await PersistenceTests.SeedAsync(hub);

        var lastSnapshot = await SnapshotAsync(hub);

        Assert.Equal(firstSnapshot, lastSnapshot);
    }

    [Fact]
    public async Task Seeding_is_deterministic_across_fresh_databases()
    {
        using var first = TempHub.Create();
        using var second = TempHub.Create();

        await PersistenceTests.SeedAsync(first);
        await PersistenceTests.SeedAsync(second);

        Assert.Equal(await IdentitiesAsync(first), await IdentitiesAsync(second));
    }

    [Fact]
    public async Task The_gate_fixture_organization_is_persisted_as_blocked()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        await using var db = hub.NewContext();
        var gate = await db.Organizations.SingleAsync(o => o.Name == "Hard Isolation Gate Fixture");

        Assert.True(gate.RequiresHardIsolation);
        Assert.Equal(IsolationLevel.Logical, gate.IsolationLevel);
        Assert.Equal(OrganizationStatus.Blocked, gate.Status);
        Assert.False(gate.CanStartExecution);
    }

    [Fact]
    public async Task Removing_an_organization_from_configuration_reports_an_orphan_and_deletes_nothing()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        var trimmed = FixtureConfig.Default[..FixtureConfig.Default.IndexOf(
            "  - slug: isolation-gate-fixture", StringComparison.Ordinal)];
        await File.WriteAllTextAsync(hub.ConfigPath, trimmed);

        var report = await PersistenceTests.SeedAsync(hub);

        Assert.Contains(report.Orphans, o => o is { Entity: "Organization", Identity: "isolation-gate-fixture" });

        await using var db = hub.NewContext();
        Assert.Equal(2, await db.Organizations.CountAsync());
    }

    [Fact]
    public async Task Changed_configuration_updates_rather_than_duplicates()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);

        var renamed = FixtureConfig.Default.Replace(
            "name: Hub Development Fixture",
            "name: Hub Development Fixture (renamed)",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(hub.ConfigPath, renamed);

        var report = await PersistenceTests.SeedAsync(hub);

        Assert.Equal(1, Count(report, "Organization").Updated);
        Assert.Equal(0, report.TotalCreated);

        await using var db = hub.NewContext();
        Assert.Equal(2, await db.Organizations.CountAsync());
        Assert.Contains(await db.Organizations.ToListAsync(), o => o.Name.EndsWith("(renamed)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_enum_value_is_rejected_with_a_useful_message()
    {
        using var hub = TempHub.Create(
            FixtureConfig.Default.Replace("isolationLevel: Logical", "isolationLevel: Nonsense", StringComparison.Ordinal));

        await using var db = await hub.NewMigratedContextAsync();
        var config = new YamlHubConfigReader().Read(hub.ConfigPath);

        var error = await Assert.ThrowsAsync<HubConfigurationException>(
            () => new CatalogSeeder(new EfHubStore(db)).SeedAsync(config, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("Nonsense", error.Message, StringComparison.Ordinal);
        Assert.Contains("Logical", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Seeding_writes_one_audit_row_only_when_something_changed()
    {
        using var hub = TempHub.Create();
        await PersistenceTests.SeedAsync(hub);
        await PersistenceTests.SeedAsync(hub);

        await using var db = hub.NewContext();
        var seedEvents = await db.AuditEvents.Where(a => a.EventType == "CatalogSeeded").ToListAsync();

        Assert.Single(seedEvents);
    }

    private static SeedCounts Count(SeedReport report, string entity) =>
        report.Counts.Single(c => c.Entity == entity);

    private static async Task<string> SnapshotAsync(TempHub hub)
    {
        await using var db = hub.NewContext();

        var organizations = await db.Organizations.OrderBy(o => o.Slug).ToListAsync();
        var projects = await db.Projects.OrderBy(p => p.Slug).ToListAsync();
        var repositories = await db.Repositories.OrderBy(r => r.Slug).ToListAsync();
        var profiles = await db.VerificationProfiles.OrderBy(p => p.Name).ToListAsync();
        var agents = await db.AgentProfiles.OrderBy(a => a.Name).ToListAsync();
        var audits = await db.AuditEvents.CountAsync();

        return string.Join('\n',
            [
                .. organizations.Select(o => $"O:{o.Id}:{o.Slug}:{o.Name}:{o.Status}:{o.UpdatedAtUtc:O}"),
                .. projects.Select(p => $"P:{p.Id}:{p.Slug}:{p.UpdatedAtUtc:O}"),
                .. repositories.Select(r => $"R:{r.Id}:{r.Slug}:{r.UpdatedAtUtc:O}"),
                .. profiles.Select(p => $"V:{p.Id}:{p.Name}:{p.UpdatedAtUtc:O}"),
                .. agents.Select(a => $"A:{a.Id}:{a.Name}:{a.UpdatedAtUtc:O}"),
                $"AUDIT:{audits}",
            ]);
    }

    private static async Task<string> IdentitiesAsync(TempHub hub)
    {
        await using var db = hub.NewContext();

        var organizations = await db.Organizations.OrderBy(o => o.Slug).Select(o => o.Id).ToListAsync();
        var projects = await db.Projects.OrderBy(p => p.Slug).Select(p => p.Id).ToListAsync();
        var repositories = await db.Repositories.OrderBy(r => r.Slug).Select(r => r.Id).ToListAsync();
        var profiles = await db.VerificationProfiles.OrderBy(p => p.Name).Select(p => p.Id).ToListAsync();

        return string.Join(
            ',',
            organizations.Concat(projects).Concat(repositories).Concat(profiles).Select(g => g.ToString()));
    }
}
