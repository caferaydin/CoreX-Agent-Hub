using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A configuration that cannot be loaded must never leave a half applied catalog.
///
/// Two independent guarantees are proven here:
///   1. Parsing happens before the seeder is ever invoked, so a bad key cannot start a
///      mutation at all.
///   2. The seeder calls SaveChanges exactly once at the end, so a failure discovered
///      part way through (for example an invalid enum on the second organization) leaves
///      the earlier work in the change tracker only.
///
/// No distributed transaction and no extra infrastructure is involved.
/// </summary>
public sealed class ConfigurationAtomicityTests
{
    [Fact]
    public async Task An_unknown_property_leaves_a_seeded_catalog_untouched()
    {
        using var hub = TempHub.Create();

        await PersistenceTests.SeedAsync(hub);
        var before = await SnapshotAsync(hub);
        Assert.NotEmpty(before);

        await File.WriteAllTextAsync(
            hub.ConfigPath,
            ConfigurationValidationTests.WithUnknownKey("requiresHardIsolation: false", "unexpectedOrgSetting: hello"));

        await Assert.ThrowsAsync<HubConfigurationException>(() => PersistenceTests.SeedAsync(hub));

        Assert.Equal(before, await SnapshotAsync(hub));
    }

    [Fact]
    public async Task A_failure_discovered_part_way_through_the_seed_persists_nothing()
    {
        // The gate organization is the second entry, so the first organization has already
        // been built and tracked when the invalid enum is reached.
        using var hub = TempHub.Create(
            FixtureConfig.Default.Replace(
                "isolationLevel: Logical\n    requiresHardIsolation: true",
                "isolationLevel: Nonsense\n    requiresHardIsolation: true",
                StringComparison.Ordinal));

        await using (var db = await hub.NewMigratedContextAsync())
        {
            Assert.Empty(await db.Organizations.ToListAsync());
        }

        await Assert.ThrowsAsync<HubConfigurationException>(() => PersistenceTests.SeedAsync(hub));

        await using var verify = hub.NewContext();
        Assert.Empty(await verify.Organizations.ToListAsync());
        Assert.Empty(await verify.Projects.ToListAsync());
        Assert.Empty(await verify.Repositories.ToListAsync());
        Assert.Empty(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task The_cli_refuses_to_run_with_a_malformed_configuration()
    {
        using var hub = TempHub.Create(
            ConfigurationValidationTests.WithUnknownKey("defaultBranch: main", "remoteUrl: origin"));

        var result = await hub.RunAsync("org", "list");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("configuration error", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remoteUrl", result.StdErr, StringComparison.Ordinal);

        // A readable diagnostic, not a stack trace.
        Assert.DoesNotContain("   at ", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("YamlDotNet.Core", result.StdErr, StringComparison.Ordinal);

        // Nothing was listed, because nothing was loaded.
        Assert.DoesNotContain("hub-dev", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_configuration_still_seeds_and_stays_idempotent_after_a_rejected_one()
    {
        using var hub = TempHub.Create();

        var first = await PersistenceTests.SeedAsync(hub);
        Assert.False(first.MadeNoChanges);

        var good = await File.ReadAllTextAsync(hub.ConfigPath);
        await File.WriteAllTextAsync(
            hub.ConfigPath,
            ConfigurationValidationTests.WithUnknownKey("schemaVersion:", "unexpectedRootSetting: true"));

        await Assert.ThrowsAsync<HubConfigurationException>(() => PersistenceTests.SeedAsync(hub));

        await File.WriteAllTextAsync(hub.ConfigPath, good);
        var recovered = await PersistenceTests.SeedAsync(hub);

        Assert.True(recovered.MadeNoChanges);
    }

    [Fact]
    public async Task A_duplicate_key_leaves_a_seeded_catalog_untouched()
    {
        using var hub = TempHub.Create();

        await PersistenceTests.SeedAsync(hub);
        var before = await SnapshotAsync(hub);
        Assert.NotEmpty(before);

        await File.WriteAllTextAsync(
            hub.ConfigPath,
            ConfigurationDuplicateKeyTests.WithDuplicateKey(
                "requiresHardIsolation: true",
                "requiresHardIsolation: false"));

        await Assert.ThrowsAsync<HubConfigurationException>(() => PersistenceTests.SeedAsync(hub));

        // Row counts, values, UpdatedAtUtc stamps and the audit count are all part of
        // the snapshot, so an unchanged snapshot means zero persisted mutation.
        Assert.Equal(before, await SnapshotAsync(hub));
    }

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
                .. organizations.Select(o =>
                    $"O:{o.Id}:{o.Slug}:{o.Name}:{o.IsolationLevel}:{o.RequiresHardIsolation}:{o.Status}:{o.UpdatedAtUtc:O}"),
                .. projects.Select(p => $"P:{p.Id}:{p.Slug}:{p.TaskKeyPrefix}:{p.UpdatedAtUtc:O}"),
                .. repositories.Select(r => $"R:{r.Id}:{r.Slug}:{r.LocalPath}:{r.UpdatedAtUtc:O}"),
                .. profiles.Select(p => $"V:{p.Id}:{p.Name}:{p.IsDefault}:{p.UpdatedAtUtc:O}"),
                .. agents.Select(a => $"A:{a.Id}:{a.Name}:{a.Role}:{a.UpdatedAtUtc:O}"),
                $"AUDIT:{audits}",
            ]);
    }
}
