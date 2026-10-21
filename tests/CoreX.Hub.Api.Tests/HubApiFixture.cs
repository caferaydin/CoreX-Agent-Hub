using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CoreX.Hub.Api;
using CoreX.Hub.Composition;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Persistence;
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// A whole Hub installation with an HTTP surface in front of it.
///
/// Real database, real migrations, real application services, real routing - the only thing
/// that is not real is the provider, because these tests must never spend money or depend on
/// a model being available. Executions are therefore written directly into the store when a
/// test needs one to look at, and started for real only where starting is the thing under
/// test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HubApiFixture : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;

    private HubApiFixture(WebApplication app, HttpClient client, string root, string configPath)
    {
        _app = app;
        Client = client;
        Root = root;
        ConfigPath = configPath;
    }

    public HttpClient Client { get; }

    public string Root { get; }

    public string ConfigPath { get; }

    public HubRuntime Runtime => _app.Services.GetRequiredService<HubRuntime>();

    public static async Task<HubApiFixture> CreateAsync(
        bool allowExecutionLaunch = false,
        string? webRoot = null,
        bool serveOperatorUi = false,
        string? autopilot = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-api-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        var configPath = Path.Combine(root, "hub.config.yaml");
        File.WriteAllText(configPath, Configuration(root, autopilot), new UTF8Encoding(false));

        var app = HubApi.Build(new HubApiOptions
        {
            // An ephemeral loopback port, so tests can run beside each other. Still loopback:
            // the option cannot express anything else.
            ListenUrl = "http://127.0.0.1:0",
            DataDirectory = root,
            ConfigPath = configPath,

            // Off unless a test is about starting one. A misrouted request must never be able
            // to launch a real agent from a test run.
            AllowExecutionLaunch = allowExecutionLaunch,

            // Off unless a test supplies a build. Left on, a stray wwwroot beside the test
            // binaries would quietly change what an unmapped route answers.
            ServeOperatorUi = serveOperatorUi || webRoot is not null,
            WebRoot = webRoot,

            // Off always. A background pass deciding something while a test decides it too
            // would make both answers meaningless; autopilot tests drive passes themselves.
            RunAutopilot = false,
        });

        await app.StartAsync();

        var address = app.Urls.First();

        return new HubApiFixture(
            app,
            new HttpClient { BaseAddress = new Uri(address) },
            root,
            configPath);
    }

    public async Task<(int Status, JsonElement Body)> GetAsync(string path)
    {
        using var response = await Client.GetAsync(path);

        return ((int)response.StatusCode, await ReadAsync(response));
    }

    public async Task<(int Status, JsonElement Body)> PostAsync(string path, object? body = null)
    {
        using var response = body is null
            ? await Client.PostAsync(path, content: null)
            : await Client.PostAsJsonAsync(path, body, Json);

        return ((int)response.StatusCode, await ReadAsync(response));
    }

    /// <summary>
    /// Drives an execution to a terminal state the way the orchestrator would have.
    ///
    /// Through the aggregate's own lifecycle methods, never by writing a column: an execution
    /// that reached ReviewBlocked by assignment would let a test assert something the domain
    /// would refuse to produce. What is skipped is the provider, not the rules.
    /// </summary>
    public async Task<DomainExecution> SeedExecutionAsync(
        HubTask task,
        ExecutionOutcome outcome,
        string? resultCommitSha = "b16b00b5b16b00b5b16b00b5b16b00b5b16b00b5",
        Action<HubScope, DomainExecution>? decorate = null)
    {
        await using var scope = Runtime.OpenScope();

        var now = DateTimeOffset.UtcNow;

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            attemptNo: 1,
            baseCommitSha: "a11ce0a11ce0a11ce0a11ce0a11ce0a11ce0a11c",
            branchName: $"agent/{task.TaskKey.Value}/1",
            worktreePath: Path.Combine(Root, "work", task.TaskKey.Value),
            previousExecutionId: null,
            nowUtc: now);

        scope.Store.AddExecution(execution);

        // The same progression a real run walks, so every state the API reports is one the
        // model agrees is reachable.
        foreach (var state in new[]
                 {
                     ExecutionState.PreparingWorkspace,
                     ExecutionState.BuildingContext,
                     ExecutionState.RunningAgent,
                     ExecutionState.CapturingChanges,
                     ExecutionState.Verifying,
                     ExecutionState.Reviewing,
                 })
        {
            execution.Advance(state, now);
        }

        if (resultCommitSha is not null)
        {
            execution.RecordResultCommit(resultCommitSha, now);
        }

        execution.Complete(outcome, now);

        // The steps a terminal execution really has. Every one of them is a prerequisite some
        // acceptance route checks, so a fixture that skipped them would produce refusals that
        // say nothing about the API.
        var agent = new ExecutionStep(
            Guid.CreateVersion7(), execution.OrganizationId, execution.Id, order: 1,
            ExecutionStepKind.Agent, now);

        agent.Start(now);
        agent.Succeed(now);
        scope.Store.AddExecutionStep(agent);

        var verify = new ExecutionStep(
            Guid.CreateVersion7(), execution.OrganizationId, execution.Id, order: 2,
            ExecutionStepKind.Verify, now);

        verify.Start(now);
        verify.Succeed(now);
        scope.Store.AddExecutionStep(verify);

        scope.Store.AddCommandResult(new CommandResult(
            Guid.CreateVersion7(), execution.OrganizationId, verify.Id, order: 1,
            stepName: "build",
            executable: "dotnet",
            args: ["build"],
            status: VerificationCommandStatus.Passed,
            required: true,
            exitCode: 0,
            durationMilliseconds: 1000,
            testsPassed: null,
            testsFailed: null,
            testsSkipped: null,
            nowUtc: now));

        // A ReviewBlocked execution has something that blocked it. Seeding the outcome
        // without the finding would produce a state the reviewer could never have created,
        // and the override route is right to refuse one.
        if (outcome == ExecutionOutcome.ReviewBlocked)
        {
            var review = new ExecutionStep(
                Guid.CreateVersion7(), execution.OrganizationId, execution.Id, order: 3,
                ExecutionStepKind.Review, now);

            review.Start(now);
            review.Succeed(now);

            scope.Store.AddExecutionStep(review);

            scope.Store.AddFinding(new Finding(
                Guid.CreateVersion7(), execution.OrganizationId, review.Id,
                FindingSeverity.Major, FindingCategory.Correctness,
                "the fixture's blocking finding",
                "seeded so this execution is one the gate would really have blocked",
                "docs/seeded.md", 1, null,
                FindingConfidence.High, outOfScope: false, now));
        }

        decorate?.Invoke(scope, execution);

        // Where a real run leaves it. Without this the task is still Ready and every decision
        // route refuses - correctly, and for a reason that has nothing to do with the API.
        var owning = await scope.Store.FindTaskAsync(task.Id, CancellationToken.None);

        if (owning is not null && owning.State == TaskState.Ready && outcome != ExecutionOutcome.ReviewBlocked)
        {
            owning.MarkRunning(now);
            owning.MarkAwaitingAcceptance(now);
        }

        await scope.Store.SaveChangesAsync(CancellationToken.None);

        return execution;
    }

    /// <summary>
    /// A live execution with no process behind it - what a host that died leaves.
    ///
    /// Deliberately not terminal and deliberately unlistened-for: that is the shape the
    /// cancellation authority has to refuse rather than fake.
    /// </summary>
    public async Task<DomainExecution> SeedRunningExecutionAsync(HubTask task)
    {
        await using var scope = Runtime.OpenScope();

        var now = DateTimeOffset.UtcNow;

        var execution = new DomainExecution(
            Guid.CreateVersion7(),
            task.OrganizationId,
            task.Id,
            attemptNo: 1,
            baseCommitSha: "a11ce0a11ce0a11ce0a11ce0a11ce0a11ce0a11c",
            branchName: $"agent/{task.TaskKey.Value}/1",
            worktreePath: Path.Combine(Root, "work", task.TaskKey.Value),
            previousExecutionId: null,
            nowUtc: now);

        execution.Advance(ExecutionState.PreparingWorkspace, now);
        execution.Advance(ExecutionState.BuildingContext, now);
        execution.Advance(ExecutionState.RunningAgent, now);

        scope.Store.AddExecution(execution);

        var owning = await scope.Store.FindTaskAsync(task.Id, CancellationToken.None);

        if (owning is not null && owning.State == TaskState.Ready)
        {
            owning.MarkRunning(now);
        }

        await scope.Store.SaveChangesAsync(CancellationToken.None);

        return execution;
    }

    public async Task<HubTask> SeedTaskAsync(
        string taskKey,
        TaskState state = TaskState.Ready,
        bool withHumanCriterion = true)
    {
        await using var scope = Runtime.OpenScope();

        var organization = (await scope.Store.ListOrganizationsAsync(CancellationToken.None)).First();
        var project = (await scope.Store.ListProjectsAsync(organization.Id, CancellationToken.None)).First();
        var repository = (await scope.Store.ListRepositoriesAsync(organization.Id, project.Id, CancellationToken.None))
            .First();

        var created = await scope.Tasks.CreateAsync(
            new Application.Tasks.CreateTaskRequest
            {
                OrganizationSlug = organization.Slug.Value,
                ProjectSlug = project.Slug.Value,
                RepositorySlug = repository.Slug.Value,
                Objective = "an objective the fixture supplied",
                TaskKey = taskKey,
                AllowedScope = ["docs/**"],
                Criteria = withHumanCriterion
                    ?
                    [
                        new Application.Tasks.CriterionInput(VerificationKind.Machine, "it builds", "build"),
                        new Application.Tasks.CriterionInput(VerificationKind.Human, "it reads well"),
                    ]
                    : [new Application.Tasks.CriterionInput(VerificationKind.Machine, "it builds", "build")],
                Actor = "fixture",
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        if (state == TaskState.Ready)
        {
            await scope.Tasks.MarkReadyAsync(
                taskKey, organization.Slug.Value, "fixture", DateTimeOffset.UtcNow, CancellationToken.None);

            return (await scope.Store.FindTaskAsync(created.Task.Id, CancellationToken.None))!;
        }

        return created.Task;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not every response is json - a diff is text - and a test that asked for one
            // should see the body rather than a parser error.
            return JsonSerializer.SerializeToElement(new { raw = text });
        }
    }

    /// <summary>
    /// A one-organization installation with a deterministic runtime, so nothing here can
    /// reach a real provider even if a test asked it to.
    /// </summary>
    private static string Configuration(string root, string? autopilot) =>
        $"""
         workspace:
           root: '{Path.Combine(root, "cx").Replace("\\", "\\\\")}'

         agents:
           scriptedRuntimes:
             - key: scripted
               fixturePath: '{Path.Combine(root, "fixture.exe").Replace("\\", "\\\\")}'
               mode: clean

         organizations:
           - slug: test-org
             name: Test Org
             filesystemRoot: '{Path.Combine(root, "org").Replace("\\", "\\\\")}'
             isolationLevel: Logical
             agentProfiles:
               - name: scripted-implementer
                 role: Implementer
                 provider: Scripted
                 runtimeKey: scripted
                 permissionProfile: WorkspaceWrite
                 model: fixture
                 effort: low
                 softTimeoutSeconds: 60
                 idleTimeoutSeconds: 60
                 hardTimeoutSeconds: 120
             projects:
               - slug: test-project
                 name: Test Project
                 taskKeyPrefix: API
                 repositories:
                   - slug: test-repo
                     name: Test Repo
                     localPath: '{Path.Combine(root, "repo").Replace("\\", "\\\\")}'
                     defaultBranch: main
                     protectedBranches:
                       - main
         {Indent(autopilot)}
                     verificationProfiles:
                       - name: fixture-verify
                         isDefault: true
                         workingDirectory: '.'
                         steps:
                           - name: build
                             executable: dotnet
                             args: ['build']
                             timeoutSeconds: 600
                             required: true
                             resultParser: none
         """;

    /// <summary>
    /// The repository's autopilot block moved to sit among the repository's own keys, or
    /// nothing. Callers write it as if it started at the left margin.
    /// </summary>
    private static string Indent(string? autopilot) =>
        autopilot is null
            ? string.Empty
            : string.Join(
                "\n",
                autopilot.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => new string(' ', 12) + line.TrimEnd('\r')));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
