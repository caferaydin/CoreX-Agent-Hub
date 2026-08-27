using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A readiness probe whose answers the test decides.
///
/// Substituted rather than driven, because every one of these tests is about what the policy
/// does with an answer, and a real probe would make each of them a test about this machine.
/// The one real machine test lives in the integration suite, where it belongs.
/// </summary>
internal sealed class FakeReadinessProbe : IHardIsolationReadinessProbe
{
    private readonly Dictionary<string, HardIsolationReadiness> _answers =
        new(StringComparer.Ordinal);

    /// <summary>Every slug this probe was asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    public FakeReadinessProbe Ready(string slug)
    {
        _answers[slug] = HardIsolationReadiness.From(
            slug,
            HardIsolationChecks.Required.Select(id => ReadinessCheck.Passed(id, "Fixture.")));

        return this;
    }

    public FakeReadinessProbe NotReady(string slug, params string[] failing)
    {
        var failed = failing.ToHashSet(StringComparer.Ordinal);

        _answers[slug] = HardIsolationReadiness.From(
            slug,
            HardIsolationChecks.Required.Select(id => failed.Contains(id)
                ? ReadinessCheck.Failed(id, "Fixture.")
                : ReadinessCheck.Passed(id, "Fixture.")));

        return this;
    }

    public Task<HardIsolationReadiness> ProbeAsync(
        string organizationSlug,
        HardIsolationProfile profile,
        CancellationToken ct)
    {
        Asked.Add(organizationSlug);

        return Task.FromResult(_answers.TryGetValue(organizationSlug, out var answer)
            ? answer
            : HardIsolationReadiness.Unavailable(organizationSlug, "No fixture answer."));
    }
}

/// <summary>Profiles a test declares, without a configuration file.</summary>
internal sealed class FakeProfileSource : IHardIsolationProfileSource
{
    private readonly HashSet<string> _configured;

    public FakeProfileSource(params string[] configured) =>
        _configured = configured.ToHashSet(StringComparer.Ordinal);

    public bool TryGetProfile(string organizationSlug, out HardIsolationProfile? profile, out string? problem)
    {
        profile = null;
        problem = null;

        if (!_configured.Contains(organizationSlug))
        {
            problem = "no fixture profile.";
            return false;
        }

        profile = HardIsolationFixture.Profile();
        return true;
    }
}

/// <summary>An execution plane a test can switch on, which no shipped build has yet.</summary>
internal sealed class FakeExecutionPlane : IHardExecutionPlane
{
    public bool IsAvailable { get; init; }

    public string UnavailableReason => "Fixture: no execution plane.";
}

/// <summary>Shared construction for the hard isolation tests.</summary>
internal static class HardIsolationFixture
{
    /// <summary>A profile that parses. Nothing here is asserted about the machine.</summary>
    public static HardIsolationProfile Profile()
    {
        Assert.True(HardIsolationProfile.TryRead(HardIsolationFixture.Config(), out var profile, out var problem), problem);
        return profile!;
    }

    public static Configuration.HardIsolationConfig Config() => new()
    {
        Backend = "Wsl2",
        EnvironmentId = "corex-fixture",
        AgentUser = "corex-agent",
        ServiceUser = "corex-hub",
        ExecutionRoot = "/srv/corex/exec",
        EvidenceRoot = "/srv/corex/evidence",
        ProcessLimit = 64,
        MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
    };

    /// <summary>A readiness result with every check passing.</summary>
    public static HardIsolationReadiness AllPassing(string slug) =>
        HardIsolationReadiness.From(
            slug, HardIsolationChecks.Required.Select(id => ReadinessCheck.Passed(id, "Fixture.")));
}

/// <summary>
/// A real catalogue on a temporary database.
///
/// The topology rule reads every organization on the machine, and a hand written stub of a
/// forty member store would be a large amount of code proving nothing. This is the store
/// production uses, over a database that lives as long as the test.
/// </summary>
internal sealed class TempCatalog : IAsyncDisposable
{
    private readonly HubDbContext _db;
    private readonly string _root;

    private TempCatalog(HubDbContext db, string root, IHubStore store)
    {
        _db = db;
        _root = root;
        Store = store;
    }

    public IHubStore Store { get; }

    public static async Task<TempCatalog> CreateAsync(params (string Slug, IsolationLevel Level, bool RequiresHard)[] organizations)
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-isolation-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        var db = HubDatabase.CreateContext(
            Path.Combine(root, HubDatabase.DatabaseFileName), new OrganizationScope());

        await HubDatabase.MigrateAsync(db, CancellationToken.None);

        var store = new EfHubStore(db);
        var now = DateTimeOffset.Parse("2026-01-01T09:00:00Z");

        foreach (var (slug, level, requiresHard) in organizations)
        {
            store.AddOrganization(new Organization(
                Guid.CreateVersion7(),
                Slug.Parse(slug),
                slug,
                Path.Combine(root, slug),
                level,
                requiresHard,
                ["PATH"],
                now));
        }

        await store.SaveChangesAsync(CancellationToken.None);

        return new TempCatalog(db, root, store);
    }

    public async Task<Organization> FindAsync(string slug) =>
        await Store.FindOrganizationBySlugAsync(slug, CancellationToken.None)
        ?? throw new InvalidOperationException($"fixture organization '{slug}' is missing.");

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must never fail a test run.
        }
    }
}
