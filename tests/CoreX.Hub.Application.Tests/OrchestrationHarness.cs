using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Composition.Context;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// The reviewer, when this run is reviewed at all.
    ///
    /// Null is the honest default and not a convenience: most of these tests are about the
    /// stages before the review, and a repository that names no reviewer really does reach
    /// the Review step with nothing to run - which is a case worth exercising rather than
    /// hiding behind a stub that always passes.
    /// </summary>
    public IExecutionReviewer? Reviewer { get; set; }

    public VerificationProfile? VerificationProfile { get; set; }

    /// <summary>
    /// Builds the repository's verification profile during the seed.
    ///
    /// It has to exist before the task becomes Ready, because the ready gate refuses a
    /// Machine criterion whose verification reference names no step - which is exactly the
    /// guard that stops a task declaring machine truth nobody will ever produce.
    /// </summary>
    private Func<Guid, Guid, DateTimeOffset, VerificationProfile>? ProfileFactory { get; init; }

    /// <summary>
    /// Wraps the production workspace port so a test can watch the handoff or interrupt it.
    /// The inner port is the real one, so what is being observed is the real sequence.
    /// </summary>
    /// <summary>
    /// Wraps the store the orchestrator writes through.
    ///
    /// Only one thing needs this: proving what happens when a save is interrupted after the
    /// aggregate has already been mutated. That state cannot be produced from outside the
    /// context, and it is the state the durable finalization exists for.
    /// </summary>
    public Func<IHubStore, IHubStore>? DecorateStore { get; set; }

    public Func<IExecutionWorkspace, IExecutionWorkspace>? DecorateWorkspace { get; set; }

    /// <summary>Wraps the production verification port, for the same reason.</summary>
    public Func<IExecutionVerifier, IExecutionVerifier>? DecorateVerifier { get; set; }

    /// <summary>Wraps the production context builder, for the same reason.</summary>
    public Func<IExecutionContextBuilder, IExecutionContextBuilder>? DecorateContext { get; set; }

    /// <summary>
    /// Wraps the agent gateway, the same way the workspace and the verifier can be wrapped.
    ///
    /// It exists so a test can act at the moment the agent really starts instead of guessing
    /// when that will be. Waiting a fixed time is a guess about how fast the machine is, and
    /// counting fixture processes is a guess about what the rest of the suite is doing.
    /// </summary>
    public Func<IAgentRuntimeGateway, IAgentRuntimeGateway>? DecorateAgent { get; set; }

    public Guid OrganizationId { get; private set; }

    public Guid RepositoryId { get; private set; }

    public Guid TaskId { get; private set; }

    public static string FixtureExecutable { get; } =
        Path.Combine(AppContext.BaseDirectory, "corex-fixture.exe");

    public static async Task<OrchestrationHarness> CreateAsync(
        IReadOnlyList<string>? allowedScope = null,
        IReadOnlyList<string>? forbiddenScope = null,
        IReadOnlyList<string>? humanCriteria = null,
        IReadOnlyList<(string Ref, string Text)>? machineCriteria = null,
        Func<Guid, Guid, DateTimeOffset, VerificationProfile>? verificationProfile = null)
    {
        Assert.True(GitToolchain.TryResolve(out var git, out var diagnostic), diagnostic);

        var root = Path.Combine(Path.GetTempPath(), "corex-orch-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);

        var db = HubDatabase.CreateContext(Path.Combine(root, "hub.db"), new OrganizationScope());
        await HubDatabase.MigrateAsync(db, CancellationToken.None);

        var harness = new OrchestrationHarness(root, git!, db) { ProfileFactory = verificationProfile };
        harness.InitialiseRepository();
        await harness.SeedAsync(
            allowedScope ?? ["src/**"], forbiddenScope ?? [], humanCriteria ?? [], machineCriteria ?? []);

        return harness;
    }

    private IExecutionContextBuilder Context(IExecutionContextBuilder inner) =>
        DecorateContext is { } decorate ? decorate(inner) : inner;

    /// <summary>Wires the orchestrator exactly as production composition does.</summary>
    public ExecutionOrchestrator Orchestrator()
    {
        var runtimes = new Dictionary<string, IAgentRuntime>(StringComparer.OrdinalIgnoreCase)
        {
            ["scripted"] = new ScriptedAgentRuntime(
                new ScriptedAgentOptions { FixtureExecutablePath = FixtureExecutable, Mode = Mode },
                new ProcessAgentRunner(WorkspaceRoot, WorkspaceManager.Hooks.EmptyGlobalConfigPath)),
        };

        IExecutionWorkspace workspace = new WorkspaceGateway(
            WorkspaceManager,
            _ => new RepositoryLocation(RepositoryPath, Root, WorkspaceRoot, "hub-dev", "hub-sample", "main"));

        IExecutionVerifier verifier = CreateVerificationGateway();

        return new ExecutionOrchestrator(
            DecorateStore is { } decorateStore ? decorateStore(Store) : Store,
            DecorateWorkspace is { } decorateWorkspace ? decorateWorkspace(workspace) : workspace,
            Context(new ContextGateway(
                new ContextPackBuilder(new FileSystemGuardContextPathGuard()),
                ContextRoot,
                _ => new ContextRepositoryFacts("main", ["main"]))),
            DecorateAgent is { } decorateAgent
                ? decorateAgent(new AgentGateway(runtimes, ["SystemRoot", "TEMP", "TMP", "PATH"]))
                : new AgentGateway(runtimes, ["SystemRoot", "TEMP", "TMP", "PATH"]),
            DecorateVerifier is { } decorateVerifier ? decorateVerifier(verifier) : verifier,
            new EvidenceGateway(EvidenceStore),
            GateFixtures.Gates,
            new LeaseWriter(OpenStore),
            new DetachedStore(OpenStore),
            Liveness,
            Clock,
            new ExecutionOrchestratorOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
                MaximumLeaseSilence = TimeSpan.FromMinutes(2),
            },
            Reviewer);
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

    /// <summary>
    /// Closes every context this harness opened and hands back a store on a new connection.
    ///
    /// What is read afterwards came out of the file. No change tracker, no cached entity and
    /// nothing left over from the run can make an assertion pass, which is the whole point
    /// when the question is what survives the process that produced it.
    /// </summary>
    public async Task<IHubStore> ReopenStoreAsync()
    {
        foreach (var context in _extraContexts)
        {
            await context.DisposeAsync();
        }

        _extraContexts.Clear();
        await _db.DisposeAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        return OpenStore();
    }

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

    /// <summary>
    /// What the database says, read through a store that shares nothing with the run.
    ///
    /// Not the same question as <see cref="ExecutionAsync"/>. A run whose writes all landed
    /// gives the same answer either way; a run whose last write was interrupted does not,
    /// and the durable one is the answer that matters.
    /// </summary>
    public async Task<Domain.Executions.Execution> DurableExecutionAsync(Guid id) =>
        await OpenStore().FindExecutionAsync(id, CancellationToken.None)
        ?? throw new InvalidOperationException("execution vanished");

    public async Task<HubTask> DurableTaskAsync() =>
        await OpenStore().FindTaskAsync(TaskId, CancellationToken.None)
        ?? throw new InvalidOperationException("task vanished");

    public async Task<IReadOnlyList<ExecutionStep>> DurableStepsAsync(Guid executionId) =>
        await OpenStore().ListExecutionStepsAsync(executionId, CancellationToken.None);

    /// <summary>The audit trail for one execution, read straight from the table.</summary>
    public async Task<IReadOnlyList<Domain.Audit.AuditEvent>> AuditEventsAsync(Guid executionId) =>
        await _db.AuditEvents.Where(a => a.ExecutionId == executionId).ToListAsync();

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

    private async Task SeedAsync(
        IReadOnlyList<string> allowedScope,
        IReadOnlyList<string> forbiddenScope,
        IReadOnlyList<string> humanCriteria,
        IReadOnlyList<(string Ref, string Text)> machineCriteria)
    {
        var now = Clock.GetUtcNow();

        var organization = new Organization(
            Guid.CreateVersion7(), Slug.Parse("hub-dev"), "Hub Dev", Path.Combine(Root, "fs"),
            IsolationLevel.Logical, false, ["SystemRoot", "TEMP", "TMP"], now);

        var project = new Project(Guid.CreateVersion7(), organization.Id, Slug.Parse("hub-sample"), "Sample", "HUB", now);

        var repository = new Domain.Catalog.Repository(
            Guid.CreateVersion7(), organization.Id, project.Id, Slug.Parse("hub-repo"), "Repo",
            RepositoryPath, "main", ["main"], now);

        VerificationProfile ??= ProfileFactory?.Invoke(organization.Id, repository.Id, now)
            ?? BuildVerificationProfile(organization.Id, repository.Id, "trx-fixed", 0, now);

        var profile = new AgentProfile(
            Guid.CreateVersion7(), organization.Id, ScriptedProfileName, AgentRole.Implementer,
            "Scripted", "scripted", AgentPermissionProfile.WorkspaceWrite, null, null, 60, 120, 300, now);

        var task = new HubTask(
            Guid.CreateVersion7(), organization, project, repository, TaskKey.Parse("HUB-001"),
            "Add the deterministic feature file.", allowedScope, forbiddenScope, now);

        task.AddAcceptanceCriterion(AcceptanceCriterion.Machine(
            Guid.CreateVersion7(), organization.Id, task.Id, 1, "The suite passes.", "test"));

        foreach (var machine in machineCriteria)
        {
            task.AddAcceptanceCriterion(AcceptanceCriterion.Machine(
                Guid.CreateVersion7(), organization.Id, task.Id, task.NextCriterionOrder,
                machine.Text, machine.Ref));
        }

        foreach (var human in humanCriteria)
        {
            task.AddAcceptanceCriterion(AcceptanceCriterion.Human(
                Guid.CreateVersion7(), organization.Id, task.Id, task.NextCriterionOrder, human,
                "Look at it and say."));
        }

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

/// <summary>
/// An agent gateway that says when the run reaches the agent, then runs it for real.
///
/// The signal is raised before the runtime is called and the call itself is untouched, so a
/// test can synchronise on the agent starting without changing what the agent does.
/// </summary>
internal sealed class AnnouncingAgentGateway(IAgentRuntimeGateway inner, Action onAgentStarting)
    : IAgentRuntimeGateway
{
    public Task<AgentExecutionResult> RunAsync(
        CoreX.Hub.Domain.Catalog.AgentProfile profile,
        PreparedExecutionWorkspace workspace,
        BuiltContextPack context,
        CoreX.Hub.Domain.Tasks.HubTask task,
        string correlationId,
        CancellationToken ct)
    {
        onAgentStarting();
        return inner.RunAsync(profile, workspace, context, task, correlationId, ct);
    }
}
