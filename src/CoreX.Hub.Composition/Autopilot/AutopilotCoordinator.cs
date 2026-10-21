using System.Collections.Concurrent;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Configuration;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Composition.Autopilot;

/// <summary>
/// One autopilot pass over every repository that switched one on (ADR-0028).
///
/// It decides and delegates; it owns no rule of its own. Acceptance is the acceptance
/// service's policy route, starting is the operator's Run, and whether accepted work is on
/// the default branch is a read-only git question. What lives here is only the order of
/// those three questions and the rule that ties them together: the line stops whenever
/// something needs a person, because the next task must be built on top of the last one,
/// not beside it.
///
/// A pass is safe to repeat. It reads everything it decides on from the database and the
/// configuration file, so a host that restarts between two passes loses nothing but the
/// memo of answers that cannot change - a policy refusal on a finished execution, a result
/// already found on the branch.
/// </summary>
public sealed class AutopilotCoordinator
{
    /// <summary>The actor every autopilot decision and audit row is recorded under.</summary>
    public const string Actor = "hub:autopilot";

    /// <summary>How long a refused start is left alone before it is asked again.</summary>
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromMinutes(1);

    private readonly HubRuntime _runtime;
    private readonly IAutopilotRunStarter _starter;
    private readonly IBranchLineage _lineage;
    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _pass = new(1, 1);

    // Answers that cannot change once given, so asking again would only repeat work.
    private readonly ConcurrentDictionary<Guid, string> _policyRefusals = new();
    private readonly ConcurrentDictionary<Guid, bool> _onDefaultBranch = new();
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Until, string Why)> _startRefusals = new();

    private AutopilotSnapshot _latest = AutopilotSnapshot.NotYetRun;

    public AutopilotCoordinator(
        HubRuntime runtime,
        IAutopilotRunStarter starter,
        IBranchLineage lineage,
        TimeProvider clock)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _lineage = lineage ?? throw new ArgumentNullException(nameof(lineage));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The last completed pass. Read by the status endpoint; never a source of decisions.</summary>
    public AutopilotSnapshot Latest => Volatile.Read(ref _latest);

    public async Task<AutopilotSnapshot> TickAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);

        try
        {
            var snapshot = await PassAsync(ct);
            Volatile.Write(ref _latest, snapshot);
            return snapshot;
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<AutopilotSnapshot> PassAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        HubConfig config;

        try
        {
            if (!File.Exists(_runtime.ConfigPath))
            {
                return new AutopilotSnapshot { AtUtc = now };
            }

            config = new YamlHubConfigReader().Read(_runtime.ConfigPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable file stops the autopilot; it never falls back to what it read last.
            return new AutopilotSnapshot { AtUtc = now, Problem = ex.Message };
        }

        var declared = config.Organizations
            .SelectMany(o => o.Projects.SelectMany(p => p.Repositories
                .Where(r => r.Autopilot is not null)
                .Select(r => (Organization: o, Project: p, Repository: r))))
            .ToList();

        if (declared.Count == 0)
        {
            return new AutopilotSnapshot { AtUtc = now };
        }

        var statuses = new List<AutopilotRepositoryStatus>();
        var pass = new Pass();

        foreach (var (organization, project, repository) in declared)
        {
            statuses.Add(await RepositoryPassAsync(
                organization, project, repository, repository.Autopilot!, pass, now, ct));
        }

        return new AutopilotSnapshot { AtUtc = now, Repositories = statuses, Actions = pass.Actions };
    }

    /// <summary>What one pass has done so far, shared across the repositories it visits.</summary>
    private sealed class Pass
    {
        public List<string> Actions { get; } = [];

        /// <summary>
        /// One start per pass at most: the machine has one execution slot, and a second start
        /// in the same pass could only be refused.
        /// </summary>
        public bool Started { get; set; }
    }

    private async Task<AutopilotRepositoryStatus> RepositoryPassAsync(
        OrganizationConfig organizationConfig,
        ProjectConfig projectConfig,
        RepositoryConfig repositoryConfig,
        AutopilotConfig autopilot,
        Pass pass,
        DateTimeOffset now,
        CancellationToken ct)
    {
        AutopilotRepositoryStatus Status(
            AutopilotRepositoryState state,
            string detail,
            IReadOnlyList<string>? queue = null,
            IReadOnlyList<AutopilotAttention>? attention = null) => new()
        {
            OrganizationSlug = organizationConfig.Slug,
            ProjectSlug = projectConfig.Slug,
            RepositorySlug = repositoryConfig.Slug,
            RunQueue = autopilot.RunQueue,
            AcceptWhenClean = autopilot.AcceptWhenClean,
            State = state,
            Detail = detail,
            Queue = queue ?? [],
            Attention = attention ?? [],
        };

        if (!autopilot.RunQueue && !autopilot.AcceptWhenClean)
        {
            return Status(AutopilotRepositoryState.Off, "the autopilot block switches nothing on");
        }

        await using var scope = _runtime.OpenScope();
        var store = scope.Store;

        var organization = await store.FindOrganizationBySlugAsync(organizationConfig.Slug, ct);
        var project = organization is null
            ? null
            : await store.FindProjectBySlugAsync(organization.Id, projectConfig.Slug, ct);
        var repository = project is null
            ? null
            : await store.FindRepositoryBySlugAsync(project.Id, repositoryConfig.Slug, ct);

        if (organization is null || project is null || repository is null)
        {
            return Status(AutopilotRepositoryState.Unavailable, "the catalogue has no such repository");
        }

        if (organization.Status != OrganizationStatus.Active)
        {
            return Status(AutopilotRepositoryState.Unavailable, $"the organization is {organization.Status}");
        }

        var tasks = (await store.ListTasksAsync(organization.Id, project.Id, ct))
            .Where(t => t.RepositoryId == repository.Id)
            .ToList();

        var attention = new List<AutopilotAttention>();
        var queue = new List<HubTask>();
        var running = false;

        foreach (var task in tasks)
        {
            var executions = await store.ListExecutionsForTaskAsync(task.Id, ct);
            var latest = executions.MaxBy(e => e.AttemptNo);

            if (task.State == TaskState.AwaitingAcceptance && latest is not null && autopilot.AcceptWhenClean)
            {
                await SettleAsync(scope, task, latest, organization, attention, pass.Actions, now, ct);
                continue;
            }

            switch (task.State)
            {
                case TaskState.Running:
                    running = true;
                    break;

                case TaskState.AwaitingAcceptance:
                    attention.Add(new AutopilotAttention(
                        task.TaskKey.Value,
                        AutopilotAttentionKind.WaitingForPerson,
                        "waits for a person's decision"));
                    break;

                case TaskState.Ready when latest is null:
                    queue.Add(task);
                    break;

                case TaskState.Ready:
                    attention.Add(new AutopilotAttention(
                        task.TaskKey.Value,
                        AutopilotAttentionKind.AttemptDidNotPass,
                        $"attempt {latest.AttemptNo} ended {latest.Outcome?.ToString() ?? latest.State.ToString()}; "
                        + "the autopilot does not retry",
                        task.BlockReason));
                    break;

                case TaskState.Accepted when latest?.ResultCommitSha is { } sha:
                    await CheckOnDefaultBranchAsync(organization, repository, task, latest, sha, attention, ct);
                    break;
            }
        }

        var ordered = queue
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.TaskKey.Value, StringComparer.Ordinal)
            .ToList();

        var queueKeys = ordered.Select(t => t.TaskKey.Value).ToList();

        if (running)
        {
            return Status(AutopilotRepositoryState.Working, "a task is running", queueKeys, attention);
        }

        if (attention.Count > 0)
        {
            return Status(
                AutopilotRepositoryState.Paused,
                autopilot.RunQueue ? "the queue waits until these are resolved" : "these need a person",
                queueKeys,
                attention);
        }

        if (!autopilot.RunQueue)
        {
            return Status(AutopilotRepositoryState.Idle, "accepting only; the queue is not run", queueKeys);
        }

        if (ordered.Count == 0)
        {
            return Status(AutopilotRepositoryState.Idle, "nothing is queued");
        }

        if (pass.Started || (await store.ListNonTerminalExecutionsAsync(ct)).Count > 0)
        {
            return Status(
                AutopilotRepositoryState.Waiting, "another execution holds the machine's slot", queueKeys);
        }

        var next = ordered[0];

        if (_startRefusals.TryGetValue(next.Id, out var refused) && refused.Until > now)
        {
            return Status(
                AutopilotRepositoryState.Paused,
                "the last start was refused",
                queueKeys,
                [new AutopilotAttention(next.TaskKey.Value, AutopilotAttentionKind.StartRefused, refused.Why)]);
        }

        try
        {
            var executionId = await _starter.StartAsync(next.TaskKey.Value, organization.Slug.Value, ct);
            _startRefusals.TryRemove(next.Id, out _);

            store.AddAuditEvent(new AuditEvent(
                Guid.CreateVersion7(),
                organization.Id,
                "AutopilotRunStarted",
                Actor,
                $"started {next.TaskKey} from the queue of {repository.Slug}",
                next.Id,
                executionId,
                executionId.ToString("D"),
                now));

            await store.SaveChangesAsync(ct);

            pass.Started = true;
            pass.Actions.Add($"started {next.TaskKey}");

            return Status(AutopilotRepositoryState.Working, $"started {next.TaskKey}", queueKeys.Skip(1).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _startRefusals[next.Id] = (now + StartRetryDelay, ex.Message);

            return Status(
                AutopilotRepositoryState.Paused,
                "the start was refused",
                queueKeys,
                [new AutopilotAttention(next.TaskKey.Value, AutopilotAttentionKind.StartRefused, ex.Message)]);
        }
    }

    /// <summary>
    /// A passing execution: accepted by policy if its evidence answers everything, otherwise
    /// left for a person - with the reason the policy gave, so the person knows why it is theirs.
    /// </summary>
    private async Task SettleAsync(
        HubScope scope,
        HubTask task,
        DomainExecution latest,
        Organization organization,
        List<AutopilotAttention> attention,
        List<string> actions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (await scope.Store.FindAcceptanceDecisionAsync(latest.Id, ct) is not null)
        {
            return;
        }

        if (!_policyRefusals.TryGetValue(latest.Id, out var refusal))
        {
            try
            {
                await scope.Acceptance.AcceptByPolicyAsync(
                    new PolicyAcceptanceRequest
                    {
                        TaskKey = task.TaskKey.Value,
                        AttemptNo = latest.AttemptNo,
                        OrganizationSlug = organization.Slug.Value,
                        Actor = Actor,
                    },
                    now,
                    ct);

                actions.Add($"accepted {task.TaskKey}#{latest.AttemptNo}");

                // Accepted now, so the next question is whether it is on the default branch,
                // and that is asked on the next pass with the task in its new state.
                attention.Add(new AutopilotAttention(
                    task.TaskKey.Value,
                    AutopilotAttentionKind.NotOnDefaultBranch,
                    "accepted by the autopilot; not on the default branch yet",
                    MergeHint(latest)));

                return;
            }
            catch (AcceptanceException ex)
            {
                refusal = ex.Message;
                _policyRefusals[latest.Id] = refusal;
            }
        }

        attention.Add(new AutopilotAttention(
            task.TaskKey.Value, AutopilotAttentionKind.WaitingForPerson, refusal));
    }

    private async Task CheckOnDefaultBranchAsync(
        Organization organization,
        Repository repository,
        HubTask task,
        DomainExecution latest,
        string resultCommitSha,
        List<AutopilotAttention> attention,
        CancellationToken ct)
    {
        if (_onDefaultBranch.ContainsKey(latest.Id))
        {
            return;
        }

        var contains = await _lineage.ContainsAsync(organization, repository, resultCommitSha, ct);

        switch (contains)
        {
            case true:
                _onDefaultBranch[latest.Id] = true;
                break;

            case false:
                attention.Add(new AutopilotAttention(
                    task.TaskKey.Value,
                    AutopilotAttentionKind.NotOnDefaultBranch,
                    $"accepted but not on '{repository.DefaultBranch}' yet; the next task is built on that branch",
                    MergeHint(latest)));
                break;

            default:
                attention.Add(new AutopilotAttention(
                    task.TaskKey.Value,
                    AutopilotAttentionKind.LineageUnknown,
                    $"could not tell whether '{repository.DefaultBranch}' contains the accepted result"));
                break;
        }
    }

    /// <summary>What the operator runs to take accepted work onto the default branch themselves.</summary>
    private static string MergeHint(DomainExecution execution) =>
        $"git merge --ff-only {execution.BranchName}";
}
