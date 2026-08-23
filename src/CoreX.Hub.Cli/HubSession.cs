using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Cli.Orchestration;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Cli;

/// <summary>
/// Composition root for one CLI invocation. Plain construction, no DI container
/// (architecture section 28).
/// </summary>
public sealed class HubSession : IAsyncDisposable
{
    private readonly List<HubDbContext> _extra = [];

    private HubSession(
        HubDbContext db,
        OrganizationScope scope,
        IHubStore store,
        string dataDirectory,
        string configPath,
        IExecutionWorkspaceRelease? release)
    {
        Db = db;
        Scope = scope;
        Store = store;
        DataDirectory = dataDirectory;
        ConfigPath = configPath;
        Tasks = new TaskService(store);
        Acceptance = new AcceptanceService(store, new VerificationTruthReader(store), release);
    }

    public HubDbContext Db { get; }

    public OrganizationScope Scope { get; }

    public IHubStore Store { get; }

    public TaskService Tasks { get; }

    /// <summary>The only path a human decision about an execution is recorded through.</summary>
    public AcceptanceService Acceptance { get; }

    /// <summary>
    /// Executions this process found abandoned after their result commit already existed.
    ///
    /// Reported rather than closed when the session opens, because closing one honestly needs
    /// the repository it committed into - which only a command that has resolved one can
    /// supply.
    /// </summary>
    public IReadOnlyList<PostCommitRecoveryRequired> PendingRecovery { get; private set; } = [];

    public string DataDirectory { get; }

    public string ConfigPath { get; }

    /// <summary>
    /// A second, short lived store on its own connection.
    ///
    /// The session's own store is busy for the length of whatever command is running - and
    /// during an execution it is blocked inside an agent or a verification run - while a
    /// lease heartbeat and a cancellation poll both need to read and write in the meantime.
    /// An EF context cannot be used from two places at once, so those get their own.
    /// </summary>
    public IHubStore OpenStore()
    {
        var context = HubDatabase.CreateContext(HubPaths.ResolveDatabasePath(DataDirectory), Scope);
        _extra.Add(context);
        return new EfHubStore(context);
    }

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

        // Releasing a worktree is a Windows-only path, because it drives git through the
        // Job Object based runner. Elsewhere a decision is still recorded in full and simply
        // reports that no worktree was released.
        var release = OperatingSystem.IsWindows()
            ? new SessionWorkspaceRelease(store, dataDirectory, configPath)
            : null;

        var session = new HubSession(db, scope, store, dataDirectory, configPath, release);

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

        // A fresh process is the moment to notice work nobody is driving any more. It is one
        // query when there is nothing to find, it never touches disk, and it is what makes
        // "the Hub was killed" a recoverable state rather than a row that says Running for
        // ever. Executions that got as far as a result commit are only reported here - closing
        // those out needs git and a verification run, which belong to the commands that have
        // already resolved a repository.
        var reconciler = new StaleExecutionReconciler(
            store, new ProcessLivenessProbe(), TimeProvider.System, new ExecutionOrchestratorOptions());

        var reconciled = await reconciler.ReconcileAsync(ct);

        if (reconciled.Interrupted.Count > 0 || reconciled.RecoveryRequired.Count > 0)
        {
            await diagnostics.WriteLineAsync("reconcile: " + reconciled.Describe());
        }

        session.PendingRecovery = reconciled.RecoveryRequired;

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var extra in _extra)
        {
            await extra.DisposeAsync();
        }

        _extra.Clear();
        await Db.DisposeAsync();
    }
}
