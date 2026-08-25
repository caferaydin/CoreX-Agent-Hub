using CoreX.Hub.Composition;

namespace CoreX.Hub.Api;

/// <summary>
/// Is the Hub up, and what is it configured to run with.
///
/// Health is deliberately cheap and offline: it opens the database and reads the
/// configuration, and it calls no provider, no git remote and no package registry. A health
/// endpoint that made a paid model call would be a health endpoint nobody could afford to
/// poll.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/health", async (HubRuntime runtime, CancellationToken ct) =>
        {
            var databaseReachable = false;

            try
            {
                await using var scope = runtime.OpenScope();
                await scope.Store.ListOrganizationsAsync(ct);
                databaseReachable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported as unhealthy rather than thrown: "the database did not answer" is
                // exactly what this endpoint exists to say.
            }

            var health = new HealthDto
            {
                Status = databaseReachable ? "healthy" : "unhealthy",
                DatabaseReachable = databaseReachable,
                ConfigurationLoaded = File.Exists(runtime.ConfigPath),
                PendingRecoveryCount = runtime.PendingRecovery.Count,
                NowUtc = DateTimeOffset.UtcNow,
            };

            return databaseReachable
                ? Results.Ok(health)
                : Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        api.MapGet("/agent-profiles", async (
            string? organizationSlug,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            var organizations = string.IsNullOrWhiteSpace(organizationSlug)
                ? await scope.Store.ListOrganizationsAsync(ct)
                : [
                    await scope.Store.FindOrganizationBySlugAsync(organizationSlug, ct)
                        ?? throw new ArgumentException($"organization '{organizationSlug}' was not found."),
                ];

            var config = CatalogEndpoints.ReadConfig(runtime);

            // Which runtime keys this installation declares, so a profile pointing at a
            // runtime nobody configured is visible before someone tries to run it.
            //
            // Asked as one list rather than family by family: this file has no business
            // knowing which providers exist, and a new adapter must not require an edit here
            // to become visible. Reading a declaration, not probing a provider - no process
            // is started by this endpoint.
            var declared = new HashSet<string>(
                config?.Agents.DeclaredRuntimeKeys ?? [],
                StringComparer.OrdinalIgnoreCase);

            var profiles = new List<AgentProfileDto>();

            foreach (var organization in organizations)
            {
                foreach (var profile in await scope.Store.ListAgentProfilesAsync(organization.Id, ct))
                {
                    profiles.Add(Dtos.AgentProfile(profile, declared.Contains(profile.RuntimeKey)));
                }
            }

            return Results.Ok(profiles.OrderBy(p => p.Name, StringComparer.Ordinal));
        });
    }
}
