using System.Runtime.Versioning;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api;

/// <summary>
/// Tasks: listing them, reading one, creating one, making one Ready, and starting an attempt.
///
/// Every mutation here is a thin call. Creation goes to <see cref="TaskService"/>, Ready goes
/// to the same Ready gate the CLI uses, and starting a run goes to the orchestrator through
/// <see cref="ExecutionLauncher"/>. There is no second opinion in this file about whether any
/// of those are allowed - only about how to say so over HTTP.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 200;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/tasks", async (
            Guid? organizationId,
            Guid? projectId,
            string? state,
            int? offset,
            int? limit,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            var tasks = await scope.Store.ListTasksAsync(organizationId, projectId, ct);

            if (!string.IsNullOrWhiteSpace(state))
            {
                tasks = [.. tasks.Where(t => string.Equals(t.State.ToString(), state, StringComparison.OrdinalIgnoreCase))];
            }

            // Deterministic and stable: newest first, then by key so equal timestamps never
            // reorder between two requests.
            var ordered = tasks
                .OrderByDescending(t => t.CreatedAtUtc)
                .ThenBy(t => t.TaskKey.Value, StringComparer.Ordinal)
                .ToList();

            var start = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
            var page = ordered.Skip(start).Take(take).ToList();

            var items = new List<TaskSummaryDto>(page.Count);

            foreach (var task in page)
            {
                var repository = await scope.Store.FindRepositoryAsync(task.RepositoryId, ct);

                if (repository is null)
                {
                    continue;
                }

                items.Add(Dtos.TaskSummary(
                    task, repository, await scope.Store.ListExecutionsForTaskAsync(task.Id, ct)));
            }

            return Results.Ok(new TaskPageDto(items, ordered.Count, start, take));
        });

        api.MapGet("/tasks/{taskKey}", async (
            string taskKey,
            string? organizationSlug,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            try
            {
                // The Ready gate is evaluated by the task service, freshly, exactly as the
                // CLI shows it. Nothing here reproduces its conditions.
                var view = await scope.Tasks.GetAsync(taskKey, organizationSlug, ct);
                var attempts = await scope.Store.ListExecutionsForTaskAsync(view.Task.Id, ct);

                AcceptanceStateDto? acceptance = null;
                var latest = attempts.OrderByDescending(e => e.AttemptNo).FirstOrDefault();

                if (latest is not null)
                {
                    try
                    {
                        acceptance = Dtos.Acceptance(await scope.Acceptance.PreviewAsync(
                            view.Task.TaskKey.Value, latest.AttemptNo, organizationSlug, ct));
                    }
                    catch (Application.Acceptance.AcceptanceException)
                    {
                        // A preview the Hub cannot build is not a task detail it cannot serve:
                        // the attempt may simply not be at a point where a decision exists.
                        acceptance = null;
                    }
                }

                return Results.Ok(new TaskDetailDto
                {
                    Summary = Dtos.TaskSummary(view.Task, view.Repository, attempts),
                    AllowedScope = view.Task.AllowedScope,
                    ForbiddenScope = view.Task.ForbiddenScope,
                    Criteria = [.. view.Task.AcceptanceCriteria.OrderBy(c => c.Order).Select(Dtos.Criterion)],
                    ReadyGateSatisfied = view.ReadyGate.IsSatisfied,
                    ReadyGateProblems = Problems(view.ReadyGate),
                    Attempts = [.. attempts.OrderByDescending(e => e.AttemptNo).Select(Dtos.ExecutionSummary)],
                    Acceptance = acceptance,
                });
            }
            catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
            {
                return refusal;
            }
        });

        api.MapPost("/tasks", async (CreateTaskBody body, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            try
            {
                var view = await scope.Tasks.CreateAsync(
                    new CreateTaskRequest
                    {
                        OrganizationSlug = body.OrganizationSlug,
                        ProjectSlug = body.ProjectSlug,
                        RepositorySlug = body.RepositorySlug,
                        Objective = body.Objective,
                        TaskKey = body.TaskKey,
                        AllowedScope = body.AllowedScope,
                        ForbiddenScope = body.ForbiddenScope,
                        Criteria = [.. body.Criteria.Select(Criterion)],
                        Actor = body.Actor,
                    },
                    DateTimeOffset.UtcNow,
                    ct);

                var attempts = await scope.Store.ListExecutionsForTaskAsync(view.Task.Id, ct);

                return Results.Created(
                    $"{HubApi.RoutePrefix}/tasks/{view.Task.TaskKey.Value}",
                    Dtos.TaskSummary(view.Task, view.Repository, attempts));
            }
            catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
            {
                return refusal;
            }
        });

        api.MapPost("/tasks/{taskKey}/ready", async (
            string taskKey,
            string? organizationSlug,
            ActorBody body,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            try
            {
                // Draft to Ready, decided by the gate and not by this endpoint. Already-Ready
                // is not an error: the service reports it as a non-transition, which makes a
                // repeated browser submit harmless.
                var result = await scope.Tasks.MarkReadyAsync(
                    taskKey, organizationSlug, body.Actor, DateTimeOffset.UtcNow, ct);

                var attempts = await scope.Store.ListExecutionsForTaskAsync(result.View.Task.Id, ct);

                return Results.Ok(new
                {
                    transitioned = result.Transitioned,
                    task = Dtos.TaskSummary(result.View.Task, result.View.Repository, attempts),
                    readyGateSatisfied = result.View.ReadyGate.IsSatisfied,
                    readyGateProblems = Problems(result.View.ReadyGate),
                });
            }
            catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
            {
                return refusal;
            }
        });

        api.MapPost("/tasks/{taskKey}/runs", async (
            string taskKey,
            string? organizationSlug,
            RunBody body,
            ExecutionLauncher launcher,
            HttpContext http,
            CancellationToken ct) =>
        {
            try
            {
                // Returns as soon as the execution exists. The run continues whatever this
                // connection does next.
                var started = await launcher.StartAsync(
                    taskKey, organizationSlug, body.AgentProfile, http.RequestAborted);

                return Results.Accepted($"{HubApi.RoutePrefix}/executions/{started.ExecutionId:D}", started);
            }
            catch (Composition.Orchestration.AmbiguousLaunchException ex)
            {
                return ApiErrors.Invalid(ex.Message, [.. ex.Candidates]);
            }
            catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
            {
                return refusal;
            }
        });
    }

    /// <summary>
    /// What still stops this task being Ready, in the gate's own words.
    ///
    /// Violations only. A warning is something the author should know and not something that
    /// blocks, and folding the two together would make the UI show a task as unready when the
    /// gate says otherwise.
    /// </summary>
    private static IReadOnlyList<string> Problems(ReadyGateResult gate) =>
        [.. gate.Violations.Select(v => v.ToString() ?? string.Empty)];

    private static CriterionInput Criterion(CriterionBody body) => new(
        string.Equals(body.Kind, "Machine", StringComparison.OrdinalIgnoreCase)
            ? VerificationKind.Machine
            : null,
        body.Text,
        body.VerificationRef,
        body.HumanCheckHint);
}
