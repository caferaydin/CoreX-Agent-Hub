using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Composition;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api;

/// <summary>
/// The human decisions, over HTTP.
///
/// Four routes, because there are four different decisions and collapsing them into one
/// status field would make "accept" and "reject" the same call with a different string. The
/// override is a route of its own for the same reason, one step further: it must never be
/// something that happens because a boolean defaulted the wrong way.
///
/// <see cref="AcceptanceService"/> decides all of it. Every prerequisite - latest attempt,
/// completed execution, blocking review, established machine truth, a reason that is really
/// there - is checked there, and this file would still be safe if a client were hostile.
/// </summary>
public static class AcceptanceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/tasks/{taskKey}/attempts/{attemptNo:int}/acceptance", async (
            string taskKey,
            int attemptNo,
            string? organizationSlug,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            try
            {
                // Everything a person needs in front of them, and nothing they can change.
                return Results.Ok(Dtos.Acceptance(
                    await scope.Acceptance.PreviewAsync(taskKey, attemptNo, organizationSlug, ct)));
            }
            catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
            {
                return refusal;
            }
        });

        api.MapPost("/tasks/{taskKey}/attempts/{attemptNo:int}/accept", async (
            string taskKey,
            int attemptNo,
            string? organizationSlug,
            AcceptBody body,
            HubRuntime runtime,
            CancellationToken ct) =>
            await DecideAsync(runtime, ct, scope => scope.Acceptance.AcceptAsync(
                new AcceptanceRequest
                {
                    TaskKey = taskKey,
                    AttemptNo = attemptNo,
                    OrganizationSlug = organizationSlug,
                    Actor = body.Actor,

                    // Never settable from this route. An ordinary acceptance that could carry
                    // an override flag would be an override nobody meant to ask for.
                    UseOverride = false,
                    HumanVerdicts = Verdicts(body.HumanVerdicts),
                },
                DateTimeOffset.UtcNow,
                ct)));

        api.MapPost("/tasks/{taskKey}/attempts/{attemptNo:int}/accept-override", async (
            string taskKey,
            int attemptNo,
            string? organizationSlug,
            OverrideBody body,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            // Checked here so the refusal is specific, and checked again by the service so
            // this check is a courtesy rather than the guarantee.
            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                return ApiErrors.Invalid("a review override requires a reason; it may never be silent.");
            }

            return await DecideAsync(runtime, ct, scope => scope.Acceptance.AcceptAsync(
                new AcceptanceRequest
                {
                    TaskKey = taskKey,
                    AttemptNo = attemptNo,
                    OrganizationSlug = organizationSlug,
                    Actor = body.Actor,
                    UseOverride = true,
                    Reason = body.Reason.Trim(),
                    HumanVerdicts = Verdicts(body.HumanVerdicts),
                },
                DateTimeOffset.UtcNow,
                ct));
        });

        api.MapPost("/tasks/{taskKey}/attempts/{attemptNo:int}/request-changes", async (
            string taskKey,
            int attemptNo,
            string? organizationSlug,
            DecisionBody body,
            HubRuntime runtime,
            CancellationToken ct) =>
            await DecideAsync(runtime, ct, scope => scope.Acceptance.RequestChangesAsync(
                Decision(taskKey, attemptNo, organizationSlug, body), DateTimeOffset.UtcNow, ct)));

        api.MapPost("/tasks/{taskKey}/attempts/{attemptNo:int}/reject", async (
            string taskKey,
            int attemptNo,
            string? organizationSlug,
            DecisionBody body,
            HubRuntime runtime,
            CancellationToken ct) =>
            await DecideAsync(runtime, ct, scope => scope.Acceptance.RejectAsync(
                Decision(taskKey, attemptNo, organizationSlug, body), DateTimeOffset.UtcNow, ct)));
    }

    /// <summary>
    /// One shape for all four, because they differ only in which authority method runs.
    ///
    /// Concurrency is left where it belongs: two simultaneous decisions both reach the
    /// service, and the second is refused by the rule that there is only ever one decision
    /// per execution. Nothing here serialises requests or holds a lock.
    /// </summary>
    private static async Task<IResult> DecideAsync(
        HubRuntime runtime,
        CancellationToken ct,
        Func<HubScope, Task<AcceptanceResult>> decide)
    {
        await using var scope = runtime.OpenScope();

        try
        {
            return Results.Ok(Dtos.AcceptanceResult(await decide(scope)));
        }
        catch (Exception ex) when (ApiErrors.FromException(ex) is { } refusal)
        {
            return refusal;
        }
    }

    private static TaskDecisionRequest Decision(
        string taskKey,
        int attemptNo,
        string? organizationSlug,
        DecisionBody body) => new()
        {
            TaskKey = taskKey,
            AttemptNo = attemptNo,
            OrganizationSlug = organizationSlug,
            Actor = body.Actor,
            Reason = body.Reason,
        };

    private static IReadOnlyList<HumanCriterionVerdictRecord> Verdicts(IReadOnlyList<HumanVerdictBody> bodies) =>
        [
            .. bodies.Select(v => new HumanCriterionVerdictRecord(
                v.CriterionId,
                Enum.TryParse<HumanCriterionVerdict>(v.Verdict, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new ArgumentException(
                        $"'{v.Verdict}' is not a verdict; expected Met, NotMet or NotApplicable."),
                v.Note)),
        ];
}
