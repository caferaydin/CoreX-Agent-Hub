using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Cli.Agents;
using CoreX.Hub.Cli.Context;
using CoreX.Hub.Cli.Orchestration;
using CoreX.Hub.Cli.Verification;
using CoreX.Hub.Cli.Workspace;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Infrastructure.Persistence;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// A whole Hub, wired the way production wires it: real SQLite, real git, real processes,
/// real evidence store. Only the clock and process liveness are substituted, so lease
/// behaviour can be tested without sleeping.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrchestrationHarness : IAsyncDisposable
{
    public const string ScriptedProfileName = "scripted-implementer";

    private readonly HubDbContext _db;
    private readonly List<HubDbContext> _extraContexts = [];

    private OrchestrationHarness(string root, string git, HubDbContext db)
    {
        Root = root;
        Git = git;
        _db = db;

        RepositoryPath = Path.Combine(root, "repo");
        WorkspaceRoot = Path.Combine(root, "cx");
        DataDirectory = Path.Combine(root, "data");
        ContextRoot = Path.Combine(DataDirectory, "context");

        Directory.CreateDirectory(RepositoryPath);
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(ContextRoot);

        Clock = new FakeClock(DateTimeOffset.Parse("2026-01-01T09:00:00Z"));
        Liveness = new FakeLiveness();
        Store = new EfHubStore(db);
        EvidenceStore = new EvidenceBlobStore(Path.Combine(DataDirectory, "evidence"));

        WorkspaceManager = new WorkspaceManager(
            new ProcessGitCommandExecutor(git, [root]),
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = Path.Combine(root, "control") });
    }

    public string Root { get; }

    public string Git { get; }

    public string RepositoryPath { get; }

    public string WorkspaceRoot { get; }

    public string DataDirectory { get; }

    public string ContextRoot { get; }

    public FakeClock Clock { get; }

    public FakeLiveness Liveness { get; }

    public EfHubStore Store { get; }

    public EvidenceBlobStore EvidenceStore { get; }

    public WorkspaceManager WorkspaceManager { get; }

    public ScriptedAgentMode Mode { get; set; } = ScriptedAgentMode.ApplyPatch;

    public VerificationProfile? VerificationProfile { get; set; }

    /// <summary>
    /// Wraps the production workspace port so a test can watch the handoff or interrupt it.
    /// The inner port is the real one, so what is being observed is the real sequence.
    /// </summary>
    public Func<IExecutionWorkspace, IExecutionWorkspace>? DecorateWorkspace { get; set; }

    /// <summary>Wraps the production verification port, for the same reason.</summary>
    public Func<IExecutionVerifier, IExecutionVerifier>? DecorateVerifier { get; set; }

    public Guid OrganizationId { get; private set; }

    public Guid RepositoryId { get; private set; }

    public Guid TaskId { get; private set; }

    public static string FixtureExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, "corex-fixture.exe");

    public static async Task<OrchestrationHarness> CreateAsync(
        IReadOnlyList<string>? allowedScope = null,
        IReadOnlyList<string>? forbiddenScope = null)
    {
        Assert.True(GitToolchain.TryResolve(out var git, out var diagnostic), diagnostic);

        var root = Path.Combine(Path.GetTempPath(), "corex-orch-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        var db = HubDatabase.CreateContext(Path.Combine(root, "hub.db"), new OrganizationScope());
        await HubDatabase.MigrateAsync(db, CancellationToken.None);

        var harness = new OrchestrationHarness(root, git!, db);
        harness.InitialiseRepository();
        await harness.SeedAsync(allowedScope ?? ["src/**"], forbiddenScope ?? []);

        return harness;
    }

    /// <summary>Wires the orchestrator exactly as production composition does.</summary>
    public ExecutionOrchestrator Orchestrator()
    {
        var runtimes = new Dictionary<string, IAgentRuntime>(StringComparer.OrdinalIgnoreCase)
        {
            ["scripted"] = new ScriptedAgentRuntime(
                new ScriptedAgentOptions { FixtureExecutablePath = FixtureExecutable, Mode = Mode },
                new ProcessAgentRunner(WorkspaceRoot)),
        };

        IExecutionWorkspace workspace = new WorkspaceGateway(
            WorkspaceManager,
            _ => new RepositoryLocation(RepositoryPath, Root, WorkspaceRoot, "hub-dev", "hub-sample", "main"));

        IExecutionVerifier verifier = CreateVerificationGateway();

        return new ExecutionOrchestrator(
            Store,
            DecorateWorkspace is { } decorateWorkspace ? decorateWorkspace(workspace) : workspace,
            new ContextGateway(
                new ContextPackBuilder(new FileSystemGuardContextPathGuard()),
                ContextRoot,
                _ => new ContextRepositoryFacts("main", ["main"])),
            new AgentGateway(runtimes),
            DecorateVerifier is { } decorateVerifier ? decorateVerifier(verifier) : verifier,
            new EvidenceGateway(EvidenceStore),
            GateFixtures.Gates,
            new LeaseWriter(OpenStore),
            Liveness,
            Clock,
            new ExecutionOrchestratorOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
                MaximumLeaseSilence = TimeSpan.FromMinutes(2),
            });
    }

    /// <summary>
    /// The production verification gateway, wired exactly as composition wires it.
    ///
    /// <paramref name="engineFor"/> exists so a test can decide what runs in a particular
    /// tree - the execution worktree and a disposable baseline snapshot are different roots
    /// - without replacing the gateway whose behaviour is the thing under test.
    /// </summary>
    public VerificationGateway CreateVerificationGateway(Func<string, VerificationEngine>? engineFor = null) =>
        new(
            WorkspaceManager,
            new VerificationBaselineService(new FileVerificationBaselineCache(DataDirectory), Clock),
            engineFor ?? CreateVerificationEngine,
            _ => VerificationProfile is null
                ? null
                : new VerificationSetup(
                    VerificationProfile, ["SystemRoot", "TEMP", "TMP"], RepositoryPath, Root, WorkspaceRoot));

    /// <summary>The real engine: real processes, real reports, real evidence blobs.</summary>
    public VerificationEngine CreateVerificationEngine(string root) =>
        new(
            new ProcessVerificationCommandExecutor(root),
            new FileSystemGuardVerificationFileReader(),
            new BlobEvidenceSink(EvidenceStore),
            ExecutableAllowlist.Create(
                new Dictionary<string, string> { ["fixture"] = FixtureExecutable }, out _),
            Clock);

    /// <summary>An engine whose commands come from the caller, for cancellation scenarios.</summary>
    public VerificationEngine CreateVerificationEngine(IVerificationCommandExecutor executor) =>
        new(
            executor,
            new FileSystemGuardVerificationFileReader(),
            new BlobEvidenceSink(EvidenceStore),
            ExecutableAllowlist.Create(
                new Dictionary<string, string> { ["fixture"] = FixtureExecutable }, out _),
            Clock);

    /// <summary>A second, short lived store so the heartbeat never shares a context.</summary>
    public IHubStore OpenStore()
    {
        var context = HubDatabase.CreateContext(Path.Combine(Root, "hub.db"), new OrganizationScope());
        _extraContexts.Add(context);
        return new EfHubStore(context);
    }

    public StaleExecutionReconciler Reconciler() =>
        new(Store, Liveness, Clock, new ExecutionOrchestratorOptions());

    public IReadOnlyList<string> BlobFiles() =>
        Directory.Exists(EvidenceStore.BlobsRoot)
            ? [.. Directory.EnumerateFiles(EvidenceStore.BlobsRoot, "*", SearchOption.AllDirectories)]
            : [];

    public async Task<Domain.Executions.Execution> ExecutionAsync(Guid id) =>
        await Store.FindExecutionAsync(id, CancellationToken.None)
        ?? throw new InvalidOperationException("execution vanished");

    public async Task<HubTask> TaskAsync() =>
        await Store.FindTaskAsync(TaskId, CancellationToken.None)
        ?? throw new InvalidOperationException("task vanished");

    public string RunGit(params string[] arguments) => RunGitIn(RepositoryPath, arguments);

    public string RunGitIn(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Git,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    /// <summary>
    /// A one step profile driven by the deterministic fixture, so verification is real but
    /// says exactly what the scenario needs it to.
    /// </summary>
    public static VerificationProfile BuildVerificationProfile(
        Guid organizationId,
        Guid repositoryId,
        string reportKind,
        int exitCode,
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            organizationId,
            repositoryId,
            "fixture-verification",
            true,
            ".",
            [
                new VerificationStep(
                    "test",
                    "fixture",
                    ["report", "results/results.trx", reportKind, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    120,
                    true,
                    VerificationResultParser.Trx,
                    "results/results.trx"),
            ],
            now);

    private void InitialiseRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Fixture");
        RunGit("config", "user.email", "fixture@corex.local");

        Directory.CreateDirectory(Path.Combine(RepositoryPath, "src"));
        File.WriteAllText(Path.Combine(RepositoryPath, "src", "existing.cs"), "// base\n");

        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "base");
    }

    private async Task SeedAsync(IReadOnlyList<string> allowedScope, IReadOnlyList<string> forbiddenScope)
    {
        var now = Clock.GetUtcNow();

        var organization = new Organization(
            Guid.CreateVersion7(), Slug.Parse("hub-dev"), "Hub Dev", Path.Combine(Root, "fs"),
            IsolationLevel.Logical, false, ["SystemRoot", "TEMP", "TMP"], now);

        var project = new Project(Guid.CreateVersion7(), organization.Id, Slug.Parse("hub-sample"), "Sample", "HUB", now);

        var repository = new Domain.Catalog.Repository(
            Guid.CreateVersion7(), organization.Id, project.Id, Slug.Parse("hub-repo"), "Repo",
            RepositoryPath, "main", ["main"], now);

        VerificationProfile ??= BuildVerificationProfile(organization.Id, repository.Id, "trx-fixed", 0, now);

        var profile = new AgentProfile(
            Guid.CreateVersion7(), organization.Id, ScriptedProfileName, AgentRole.Implementer,
            "Scripted", "scripted", AgentPermissionProfile.WorkspaceWrite, null, null, 60, 120, 300, now);

        var task = new HubTask(
            Guid.CreateVersion7(), organization, project, repository, TaskKey.Parse("HUB-001"),
            "Add the deterministic feature file.", allowedScope, forbiddenScope, now);

        task.AddAcceptanceCriterion(AcceptanceCriterion.Machine(
            Guid.CreateVersion7(), organization.Id, task.Id, 1, "The suite passes.", "test"));

        task.MarkReady(ReadyGate.Evaluate(task, organization, project, repository, VerificationProfile), now);

        Store.AddOrganization(organization);
        Store.AddProject(project);
        Store.AddRepository(repository);
        Store.AddAgentProfile(profile);
        Store.AddVerificationProfile(VerificationProfile);
        Store.AddTask(task);
        await Store.SaveChangesAsync(CancellationToken.None);

        OrganizationId = organization.Id;
        RepositoryId = repository.Id;
        TaskId = task.Id;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _extraContexts)
        {
            await context.DisposeAsync();
        }

        await _db.DisposeAsync();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
