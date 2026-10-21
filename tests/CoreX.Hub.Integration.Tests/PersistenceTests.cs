using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Phase 2/3 introduced a CoreX.Hub.Execution namespace, so the domain type of the
// same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Integration.Tests;

/// <summary>Migration, WAL and constraint evidence for the SQLite store (ADR-0013).</summary>
public sealed class PersistenceTests
{
    [Fact]
    public async Task Migration_applies_to_a_fresh_database_and_creates_15_tables()
    {
        using var hub = TempHub.Create();
        await using var db = await hub.NewMigratedContextAsync();

        Assert.True(File.Exists(hub.DatabasePath));

        // The second migration made verification step truth durable and the third recorded who
        // took a decision (ADR-0028). Both added columns to an existing table and no table of
        // their own, which is what this count is here to catch.
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Equal(3, applied.Count);
        Assert.EndsWith("InitialCreate", applied[0], StringComparison.Ordinal);
        Assert.EndsWith("PersistVerificationCommandTruth", applied[1], StringComparison.Ordinal);
        Assert.EndsWith("RecordAcceptanceDecisionSource", applied[2], StringComparison.Ordinal);

        var tables = await ReadUserTablesAsync(db);

        string[] expected =
        [
            "AcceptanceCriteria", "AcceptanceDecisions", "AgentProfiles", "AuditEvents",
            "CommandResults", "EvidenceArtifacts", "ExecutionSteps", "Executions",
            "Findings", "OrchestrationEvents", "Organizations", "Projects",
            "Repositories", "Tasks", "VerificationProfiles",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
        Assert.Equal(15, tables.Count);
    }

    [Fact]
    public async Task Connections_run_in_WAL_mode_with_foreign_keys_enforced()
    {
        using var hub = TempHub.Create();
        await using var db = await hub.NewMigratedContextAsync();

        var journalMode = await HubDatabase.ReadJournalModeAsync(db, CancellationToken.None);
        Assert.Equal("wal", journalMode, ignoreCase: true);

        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt32(await command.ExecuteScalarAsync(), null);
        await db.Database.CloseConnectionAsync();

        Assert.Equal(1, foreignKeys);
    }

    [Fact]
    public async Task Organization_slug_is_unique()
    {
        using var hub = TempHub.Create();
        await using var db = await hub.NewMigratedContextAsync();

        db.Organizations.Add(NewOrganization("dup-org"));
        await db.SaveChangesAsync();

        db.Organizations.Add(NewOrganization("dup-org"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Repository_local_path_is_unique()
    {
        using var hub = TempHub.Create();
        await SeedAsync(hub);

        await using var db = hub.NewContext();
        var repository = await db.Repositories.FirstAsync();

        db.Repositories.Add(new Domain.Catalog.Repository(
            Guid.CreateVersion7(),
            repository.OrganizationId,
            repository.ProjectId,
            Slug.Parse("clone-of-sample"),
            "clone",
            repository.LocalPath,
            "main",
            ["main"],
            DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Task_key_is_unique_within_a_project()
    {
        using var hub = TempHub.Create();

        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "first", "--allow", "src/**", "--human-criterion", "looks right");
        Assert.Equal(0, created.ExitCode);

        var duplicate = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample", "--key", "HUB-001",
            "--objective", "second", "--allow", "src/**", "--human-criterion", "looks right");

        Assert.Equal(2, duplicate.ExitCode);
        Assert.Contains("already exists", duplicate.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Optimistic_concurrency_rejects_a_stale_task_update()
    {
        using var hub = TempHub.Create();

        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "concurrency", "--allow", "src/**", "--machine-criterion", "test=all tests pass");
        Assert.Equal(0, created.ExitCode);

        await using var first = hub.NewContext();
        await using var second = hub.NewContext();

        var a = await first.Tasks.FirstAsync();
        var b = await second.Tasks.FirstAsync();

        a.SetBlockReason("blocked by first writer", DateTimeOffset.UtcNow);
        await first.SaveChangesAsync();

        b.SetBlockReason("blocked by second writer", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Acceptance_decision_is_unique_per_execution()
    {
        using var hub = TempHub.Create();

        var created = await hub.RunAsync(
            "task", "create", "--org", "hub-dev", "--project", "hub-sample",
            "--objective", "decision", "--allow", "src/**", "--human-criterion", "looks right");
        Assert.Equal(0, created.ExitCode);

        await using var db = hub.NewContext();
        var task = await db.Tasks.FirstAsync();

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            attemptNo: 1,
            baseCommitSha: new string('a', 40),
            branchName: "agent/HUB-001/1",
            worktreePath: @"D:\cx\_fixtures\hub-dev\hub-sample\HUB-001-1",
            previousExecutionId: null,
            DateTimeOffset.UtcNow);

        db.Executions.Add(execution);
        db.AcceptanceDecisions.Add(NewDecision(execution));
        await db.SaveChangesAsync();

        db.AcceptanceDecisions.Add(NewDecision(execution));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static AcceptanceDecision NewDecision(DomainExecution execution) =>
        new(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            execution.Id,
            AcceptanceDecisionKind.Accept,
            "tester",
            reason: null,
            overrideUsed: false,
            [],
            DateTimeOffset.UtcNow);

    private static Organization NewOrganization(string slug) =>
        new(
            Guid.CreateVersion7(),
            Slug.Parse(slug),
            slug,
            $@"D:\cx\_fixtures\{slug}",
            IsolationLevel.Logical,
            requiresHardIsolation: false,
            ["PATH"],
            DateTimeOffset.UtcNow);

    internal static async Task<SeedReport> SeedAsync(TempHub hub)
    {
        await using var db = await hub.NewMigratedContextAsync();
        var config = new YamlHubConfigReader().Read(hub.ConfigPath);
        return await new CatalogSeeder(new EfHubStore(db))
            .SeedAsync(config, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<List<string>> ReadUserTablesAsync(HubDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var connection = db.Database.GetDbConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock') ORDER BY name;";

            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
