using CoreX.Hub.Composition.Autopilot;

namespace CoreX.Hub.Api;

/// <summary>
/// What the autopilot is doing, read-only (ADR-0028).
///
/// There is no route that switches it on, pauses it or tells it what to start: the
/// configuration file is the only switch, and a person who wants a task run presses Run.
/// </summary>
public static class AutopilotEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/autopilot", (AutopilotCoordinator coordinator, HubApiOptions options) =>
        {
            var latest = coordinator.Latest;
            var ran = latest.AtUtc != AutopilotSnapshot.NotYetRun.AtUtc;

            return Results.Ok(new AutopilotDto
            {
                Running = options.RunAutopilot,
                LastPassUtc = ran ? latest.AtUtc : null,
                Problem = latest.Problem,
                Actions = latest.Actions,
                Repositories =
                [
                    .. latest.Repositories.Select(r => new AutopilotRepositoryDto
                    {
                        OrganizationSlug = r.OrganizationSlug,
                        ProjectSlug = r.ProjectSlug,
                        RepositorySlug = r.RepositorySlug,
                        RunQueue = r.RunQueue,
                        AcceptWhenClean = r.AcceptWhenClean,
                        State = r.State.ToString(),
                        Detail = r.Detail,
                        Queue = r.Queue,
                        Attention =
                        [
                            .. r.Attention.Select(a =>
                                new AutopilotAttentionDto(a.TaskKey, a.Kind.ToString(), a.Detail, a.Hint)),
                        ],
                    }),
                ],
            });
        });
    }
}
