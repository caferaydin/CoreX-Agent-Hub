using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Cli;

/// <summary>
/// Composition root for one CLI invocation. Plain construction, no DI container
/// (architecture section 28).
/// </summary>
public sealed class HubSession : IAsyncDisposable
{
    private HubSession(HubDbContext db, OrganizationScope scope, IHubStore store, string dataDirectory, string configPath)
    {
        Db = db;
        Scope = scope;
        Store = store;
        DataDirectory = dataDirectory;
        ConfigPath = configPath;
        Tasks = new TaskService(store);
        Acceptance = new AcceptanceService(store, new VerificationTruthReader(store));
    }

    public HubDbContext Db { get; }

    public OrganizationScope Scope { get; }

    public IHubStore Store { get; }

    public TaskService Tasks { get; }

    /// <summary>The only path a human decision about an execution is recorded through.</summary>
    public AcceptanceService Acceptance { get; }

    public string DataDirectory { get; }

    public string ConfigPath { get; }

    /// <summary>
    /// Opens the database, applies migrations and runs the deterministic catalog seed
    /// (architecture section 21). Seeding is idempotent and never deletes.
    /// </summary>
    public static async Task<HubSession> OpenAsync(
        string? dataDirOption,
        string? configOption,
        TextWriter diagnostics,
        CancellationToken ct)
    {
        var dataDirectory = HubPaths.ResolveDataDirectory(dataDirOption);
        Directory.CreateDirectory(dataDirectory);

        var databasePath = HubPaths.ResolveDatabasePath(dataDirectory);
        var configPath = HubPaths.ResolveConfigPath(configOption, dataDirectory);

        var scope = new OrganizationScope();
        var db = HubDatabase.CreateContext(databasePath, scope);
        await HubDatabase.MigrateAsync(db, ct);

        var store = new EfHubStore(db);
        var session = new HubSession(db, scope, store, dataDirectory, configPath);

        if (File.Exists(configPath))
        {
            var config = new YamlHubConfigReader().Read(configPath);
            var report = await new CatalogSeeder(store).SeedAsync(config, DateTimeOffset.UtcNow, ct);

            if (!report.MadeNoChanges)
            {
                await diagnostics.WriteLineAsync(
                    $"seed: {report.TotalCreated} created, {report.TotalUpdated} updated ({configPath})");
            }

            foreach (var orphan in report.Orphans)
            {
                await diagnostics.WriteLineAsync(
                    $"seed: warning: {orphan.Entity} '{orphan.Identity}' exists in the database but not in configuration; not removed.");
            }
        }
        else
        {
            await diagnostics.WriteLineAsync($"seed: skipped, no configuration file at {configPath}");
        }

        return session;
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
