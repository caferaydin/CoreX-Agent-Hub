using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Application.Verification;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Composition;

/// <summary>
/// The same composition, for a caller that outlives one command.
///
/// <see cref="HubSession"/> is built for a process that opens the database, does one thing
/// and exits: it migrates, seeds, reconciles and then holds a single EF context for the rest
/// of its life. That is exactly right for the CLI and exactly wrong for a host serving
/// concurrent requests, because an EF context cannot be used from two places at once.
///
/// So the expensive, once-per-process work stays where it was - it runs here at startup,
/// through the same <see cref="HubSession"/> the CLI uses, so neither adapter can drift into
/// a different idea of what opening the Hub means - and everything after it is a short scope
/// with its own connection.
///
/// What this deliberately is not: a second set of application services. A scope hands back
/// the same <see cref="TaskService"/> and <see cref="AcceptanceService"/> the CLI constructs,
/// over the same store. There is no HTTP-flavoured authority anywhere below this line.
/// </summary>
public sealed class HubRuntime
{
    private HubRuntime(
        string dataDirectory,
        string configPath,
        IReadOnlyList<PostCommitRecoveryRequired> pendingRecovery)
    {
        DataDirectory = dataDirectory;
        ConfigPath = configPath;
        PendingRecovery = pendingRecovery;
    }

    public string DataDirectory { get; }

    public string ConfigPath { get; }

    /// <summary>What the startup reconcile found, kept so a diagnostic endpoint can report it.</summary>
    public IReadOnlyList<PostCommitRecoveryRequired> PendingRecovery { get; }

    /// <summary>
    /// Opens the installation once: migrate, seed, reconcile.
    ///
    /// Runs through <see cref="HubSession.OpenAsync"/> rather than repeating its steps, and
    /// then lets that session go. Holding it would mean the host kept one context alive for
    /// its whole life, which is the thing this type exists to avoid.
    /// </summary>
    public static async Task<HubRuntime> StartAsync(
        string? dataDirectory,
        string? configPath,
        TextWriter diagnostics,
        CancellationToken ct)
    {
        await using var startup = await HubSession.OpenAsync(dataDirectory, configPath, diagnostics, ct);

        return new HubRuntime(startup.DataDirectory, startup.ConfigPath, startup.PendingRecovery);
    }

    /// <summary>
    /// One request's worth of database access. Disposed with the request.
    ///
    /// Every scope gets its own connection and its own organization scope object, so two
    /// requests in flight never share tracked state.
    /// </summary>
    public HubScope OpenScope()
    {
        var scope = new OrganizationScope();
        var context = HubDatabase.CreateContext(HubPaths.ResolveDatabasePath(DataDirectory), scope);

        return new HubScope(context, scope, DataDirectory, ConfigPath);
    }
}

/// <summary>One short-lived unit of Hub access, with the application services built over it.</summary>
public sealed class HubScope : IAsyncDisposable
{
    private readonly HubDbContext _context;

    private static IHubStore Open(string dataDirectory, OrganizationScope scope) =>
        new EfHubStore(HubDatabase.CreateContext(HubPaths.ResolveDatabasePath(dataDirectory), scope));

    internal HubScope(HubDbContext context, OrganizationScope scope, string dataDirectory, string configPath)
    {
        _context = context;
        Scope = scope;
        Store = new EfHubStore(context);

        // Releasing a worktree drives git through the Job Object runner, which is a Windows
        // path. Elsewhere a decision is still recorded in full and simply reports that no
        // worktree was released - the same rule the CLI follows.
        var release = OperatingSystem.IsWindows()
            ? new SessionWorkspaceRelease(Store, dataDirectory, configPath)
            : null;

        Tasks = new TaskService(Store);
        Acceptance = new AcceptanceService(Store, new VerificationTruthReader(Store), release);
        Cancellation = HubCancellation.Build(() => Open(dataDirectory, scope));
    }

    public OrganizationScope Scope { get; }

    public IHubStore Store { get; }

    public TaskService Tasks { get; }

    /// <summary>
    /// The operator's "stop this" decision, built the one way it is built anywhere.
    ///
    /// Not a new authority - <see cref="TaskCancellationService"/> already owns every rule,
    /// and has since Phase 5. What this is here for is that constructing it wrongly is
    /// possible and quiet: a caller that passed no signal would still compile, still answer,
    /// and would silently be unable to stop a running execution. One construction means the
    /// console and the HTTP surface cannot differ on that.
    /// </summary>
    public TaskCancellationService Cancellation { get; }

    /// <summary>The only path a human decision about an execution is recorded through.</summary>
    public AcceptanceService Acceptance { get; }

    public ValueTask DisposeAsync() => _context.DisposeAsync();
}

/// <summary>
/// How the cancellation authority is constructed, in one place.
///
/// The signal is the part worth being careful about. It is a named cross-process primitive,
/// which is a Windows facility here, and it is optional by design - the service treats its
/// absence as "cannot stop running work" rather than pretending. Deciding that per caller
/// would mean two adapters could disagree about whether cancelling a live run is even
/// possible, so the decision is made here and nowhere else.
/// </summary>
public static class HubCancellation
{
    /// <summary>
    /// The cross-process signal, or null where there is none.
    ///
    /// Two callers need it and they need it for opposite reasons: a runner listens so its
    /// execution can be stopped, and an operator asks so it stops. Both are the same named
    /// primitive, and building it in one place is what makes "started over HTTP" and
    /// "started at a terminal" equally cancellable.
    /// </summary>
    public static IExecutionCancellationSignal? Signal() =>
        OperatingSystem.IsWindows() ? new NamedExecutionCancellation() : null;

    /// <summary>The operator's decision, built the one way it is built anywhere.</summary>
    public static TaskCancellationService Build(Func<IHubStore> open) =>
        new(open, Signal(), TimeProvider.System);
}
