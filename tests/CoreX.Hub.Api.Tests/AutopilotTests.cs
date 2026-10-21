using System.Runtime.Versioning;
using CoreX.Hub.Composition.Autopilot;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// ADR-0028: the autopilot's passes over a real installation - real configuration file, real
/// database, the real acceptance service. Only the start and the git lineage question are
/// stand-ins, so no agent runs and no repository is needed.
///
/// What is worth proving is mostly where the line stops: every case in which the next task
/// must not start, because something needs a person first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutopilotTests
{
    private const string FullAutopilot = """
                                         autopilot:
                                           runQueue: true
                                           acceptWhenClean: true
                                         """;

    [Fact]
    public async Task Nothing_happens_where_no_repository_switches_an_autopilot_on()
    {
        await using var hub = await HubApiFixture.CreateAsync();
        await hub.SeedTaskAsync("API-001", withHumanCriterion: false);

        var (coordinator, starter, _) = Coordinator(hub);
        var pass = await coordinator.TickAsync(CancellationToken.None);

        Assert.Empty(pass.Repositories);
        Assert.Empty(pass.Actions);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task The_oldest_task_that_was_never_attempted_starts_and_only_one_at_a_time()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        await hub.SeedTaskAsync("API-001", withHumanCriterion: false);
        await hub.SeedTaskAsync("API-002", withHumanCriterion: false);

        var (coordinator, starter, _) = Coordinator(hub);

        var first = await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(["API-001"], starter.Started);
        Assert.Equal(["started API-001"], first.Actions);

        var repository = Assert.Single(first.Repositories);
        Assert.Equal(AutopilotRepositoryState.Working, repository.State);
        Assert.Equal(["API-002"], repository.Queue);

        // The run is in flight, so the next pass starts nothing.
        var second = await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(["API-001"], starter.Started);
        Assert.Equal(AutopilotRepositoryState.Working, Assert.Single(second.Repositories).State);

        // The start is on record under the autopilot's own name.
        var audit = await AuditAsync(hub, "AutopilotRunStarted");
        Assert.Equal(Composition.Autopilot.AutopilotCoordinator.Actor, Assert.Single(audit).Actor);
    }

    [Fact]
    public async Task Clean_reviewed_work_is_accepted_by_policy_and_the_queue_waits_for_the_branch()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        var task = await hub.SeedTaskAsync("API-001", withHumanCriterion: false);
        await hub.SeedTaskAsync("API-002", withHumanCriterion: false);
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed, decorate: Reviewed);

        var (coordinator, starter, lineage) = Coordinator(hub);

        var pass = await coordinator.TickAsync(CancellationToken.None);

        Assert.Contains("accepted API-001#1", pass.Actions);

        await using (var scope = hub.Runtime.OpenScope())
        {
            var decision = await scope.Store.FindAcceptanceDecisionAsync(execution.Id, CancellationToken.None);
            Assert.NotNull(decision);
            Assert.Equal(AcceptanceDecisionSource.Policy, decision.Source);
            Assert.Equal(TaskState.Accepted, (await scope.Store.FindTaskAsync(task.Id, CancellationToken.None))!.State);
        }

        // Accepted is not on main. The next task would be built on a base without it, so the
        // line stops and says what the operator runs.
        var repository = Assert.Single(pass.Repositories);
        Assert.Equal(AutopilotRepositoryState.Paused, repository.State);

        var waiting = Assert.Single(repository.Attention);
        Assert.Equal(AutopilotAttentionKind.NotOnDefaultBranch, waiting.Kind);
        Assert.Equal("git merge --ff-only agent/API-001/1", waiting.Hint);
        Assert.Empty(starter.Started);

        // Still not there on the next pass, asked of git this time.
        lineage.Answers[execution.ResultCommitSha!] = false;
        var still = await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(AutopilotRepositoryState.Paused, Assert.Single(still.Repositories).State);
        Assert.Empty(starter.Started);

        // Once the operator has taken it onto the branch, the queue moves on.
        lineage.Answers[execution.ResultCommitSha!] = true;
        var moved = await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(["API-002"], starter.Started);
        Assert.Equal(AutopilotRepositoryState.Working, Assert.Single(moved.Repositories).State);
    }

    [Fact]
    public async Task Work_with_a_human_criterion_waits_for_a_person_and_holds_the_queue()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        var task = await hub.SeedTaskAsync("API-001");
        await hub.SeedTaskAsync("API-002", withHumanCriterion: false);
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed, decorate: Reviewed);

        var (coordinator, starter, _) = Coordinator(hub);
        var pass = await coordinator.TickAsync(CancellationToken.None);

        await AssertUndecidedAsync(hub, execution.Id);

        var repository = Assert.Single(pass.Repositories);
        Assert.Equal(AutopilotRepositoryState.Paused, repository.State);

        var waiting = Assert.Single(repository.Attention);
        Assert.Equal(AutopilotAttentionKind.WaitingForPerson, waiting.Kind);
        Assert.Contains("human criteria", waiting.Detail, StringComparison.Ordinal);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task Unreviewed_work_waits_for_a_person()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        var task = await hub.SeedTaskAsync("API-001", withHumanCriterion: false);
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var (coordinator, _, _) = Coordinator(hub);
        var pass = await coordinator.TickAsync(CancellationToken.None);

        await AssertUndecidedAsync(hub, execution.Id);
        Assert.Equal(
            AutopilotAttentionKind.WaitingForPerson,
            Assert.Single(Assert.Single(pass.Repositories).Attention).Kind);
    }

    [Fact]
    public async Task An_attempt_that_did_not_pass_holds_the_queue_and_is_not_retried()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        var task = await hub.SeedTaskAsync("API-001", withHumanCriterion: false);
        await hub.SeedTaskAsync("API-002", withHumanCriterion: false);
        await hub.SeedExecutionAsync(task, ExecutionOutcome.ReviewBlocked);

        var (coordinator, starter, _) = Coordinator(hub);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var pass = await coordinator.TickAsync(CancellationToken.None);
            var repository = Assert.Single(pass.Repositories);

            Assert.Equal(AutopilotRepositoryState.Paused, repository.State);
            Assert.Equal(AutopilotAttentionKind.AttemptDidNotPass, Assert.Single(repository.Attention).Kind);
            Assert.Contains("ReviewBlocked", repository.Attention[0].Detail, StringComparison.Ordinal);
        }

        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task A_repository_that_only_accepts_never_starts_anything()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: """
                                                                         autopilot:
                                                                           acceptWhenClean: true
                                                                         """);
        await hub.SeedTaskAsync("API-001", withHumanCriterion: false);

        var (coordinator, starter, _) = Coordinator(hub);
        var pass = await coordinator.TickAsync(CancellationToken.None);

        var repository = Assert.Single(pass.Repositories);
        Assert.Equal(AutopilotRepositoryState.Idle, repository.State);
        Assert.Equal(["API-001"], repository.Queue);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task A_repository_that_does_not_accept_leaves_passing_work_for_a_person()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: """
                                                                         autopilot:
                                                                           runQueue: true
                                                                         """);
        var task = await hub.SeedTaskAsync("API-001", withHumanCriterion: false);
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed, decorate: Reviewed);

        var (coordinator, _, _) = Coordinator(hub);
        var pass = await coordinator.TickAsync(CancellationToken.None);

        await AssertUndecidedAsync(hub, execution.Id);
        Assert.Equal(
            AutopilotAttentionKind.WaitingForPerson,
            Assert.Single(Assert.Single(pass.Repositories).Attention).Kind);
    }

    [Fact]
    public async Task A_refused_start_is_reported_and_not_asked_again_at_once()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);
        await hub.SeedTaskAsync("API-001", withHumanCriterion: false);

        var (coordinator, starter, _) = Coordinator(hub);
        starter.Refusal = "another Hub process is already running an execution";

        var first = await coordinator.TickAsync(CancellationToken.None);
        var second = await coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(1, starter.Attempts);

        foreach (var pass in new[] { first, second })
        {
            var attention = Assert.Single(Assert.Single(pass.Repositories).Attention);
            Assert.Equal(AutopilotAttentionKind.StartRefused, attention.Kind);
            Assert.Equal(starter.Refusal, attention.Detail);
        }
    }

    [Fact]
    public async Task The_status_route_reports_a_host_that_does_not_run_the_autopilot()
    {
        await using var hub = await HubApiFixture.CreateAsync(autopilot: FullAutopilot);

        var (status, body) = await hub.GetAsync("/api/v1/autopilot");

        Assert.Equal(200, status);
        Assert.False(body.GetProperty("running").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("lastPassUtc").ValueKind);
        Assert.Equal(0, body.GetProperty("repositories").GetArrayLength());
    }

    /// <summary>The Review step a real reviewed run leaves behind, clean.</summary>
    private static void Reviewed(Composition.HubScope scope, DomainExecution execution)
    {
        var now = DateTimeOffset.UtcNow;

        var review = new ExecutionStep(
            Guid.CreateVersion7(), execution.OrganizationId, execution.Id, order: 3,
            ExecutionStepKind.Review, now);

        review.Start(now);
        review.Succeed(now);
        scope.Store.AddExecutionStep(review);
    }

    private static (AutopilotCoordinator Coordinator, RecordingStarter Starter, ScriptedLineage Lineage)
        Coordinator(HubApiFixture hub)
    {
        var starter = new RecordingStarter(hub);
        var lineage = new ScriptedLineage();

        return (new AutopilotCoordinator(hub.Runtime, starter, lineage, TimeProvider.System), starter, lineage);
    }

    private static async Task AssertUndecidedAsync(HubApiFixture hub, Guid executionId)
    {
        await using var scope = hub.Runtime.OpenScope();
        Assert.Null(await scope.Store.FindAcceptanceDecisionAsync(executionId, CancellationToken.None));
    }

    private static async Task<List<Domain.Audit.AuditEvent>> AuditAsync(HubApiFixture hub, string eventType)
    {
        await using var db = Infrastructure.Persistence.HubDatabase.CreateContext(
            Path.Combine(hub.Root, "hub.db"), new Application.Abstractions.OrganizationScope());

        return await db.AuditEvents.Where(a => a.EventType == eventType).ToListAsync();
    }

    /// <summary>
    /// Records what the autopilot asked to start, and leaves a live execution behind the way a
    /// real start would - so the next pass sees a running task, not a queue that never moved.
    /// </summary>
    private sealed class RecordingStarter(HubApiFixture hub) : IAutopilotRunStarter
    {
        public List<string> Started { get; } = [];

        public int Attempts { get; private set; }

        public string? Refusal { get; set; }

        public async Task<Guid> StartAsync(string taskKey, string organizationSlug, CancellationToken ct)
        {
            Attempts++;

            if (Refusal is not null)
            {
                throw new InvalidOperationException(Refusal);
            }

            Started.Add(taskKey);

            HubTask task;
            await using (var scope = hub.Runtime.OpenScope())
            {
                task = (await scope.Store.FindTasksByKeyAsync(taskKey, ct)).Single();
            }

            return (await hub.SeedRunningExecutionAsync(task)).Id;
        }
    }

    /// <summary>The lineage answer, set by the test. Unknown commits are "could not tell".</summary>
    private sealed class ScriptedLineage : IBranchLineage
    {
        public Dictionary<string, bool?> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool?> ContainsAsync(
            Organization organization,
            Repository repository,
            string commitSha,
            CancellationToken ct) =>
            Task.FromResult(Answers.TryGetValue(commitSha, out var answer) ? answer : null);
    }
}
