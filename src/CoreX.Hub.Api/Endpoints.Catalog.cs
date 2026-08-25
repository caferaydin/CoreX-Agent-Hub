using CoreX.Hub.Composition;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Api;

/// <summary>
/// Navigating the catalogue: what organizations exist, what they own, and how a repository
/// is set up here.
///
/// Read-only, and narrow on purpose. The reviewer profile and the verification profiles are
/// here because the future UI has to show what will happen to a task; the repository's local
/// path is not, because a browser response is not where a filesystem layout should surface.
/// </summary>
public static class CatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/organizations", async (HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            return Results.Ok((await scope.Store.ListOrganizationsAsync(ct))
                .OrderBy(o => o.Slug.Value, StringComparer.Ordinal)
                .Select(Dtos.Organization));
        });

        api.MapGet("/organizations/{slug}", async (string slug, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();
            var organization = await scope.Store.FindOrganizationBySlugAsync(slug, ct);

            return organization is null
                ? ApiErrors.NotFound($"organization '{slug}'")
                : Results.Ok(Dtos.Organization(organization));
        });

        api.MapGet("/projects", async (Guid? organizationId, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            return Results.Ok((await scope.Store.ListProjectsAsync(organizationId, ct))
                .OrderBy(p => p.Slug.Value, StringComparer.Ordinal)
                .Select(Dtos.Project));
        });

        api.MapGet("/repositories", async (
            Guid? organizationId,
            Guid? projectId,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            var repositories = await scope.Store.ListRepositoriesAsync(organizationId, projectId, ct);
            var config = ReadConfig(runtime);
            var result = new List<RepositoryDto>(repositories.Count);

            foreach (var repository in repositories.OrderBy(r => r.Slug.Value, StringComparer.Ordinal))
            {
                result.Add(Dtos.Repository(
                    repository,
                    ReviewerProfileOf(config, repository.Slug.Value),
                    await scope.Store.ListVerificationProfilesAsync(repository.Id, ct)));
            }

            return Results.Ok(result);
        });

        api.MapGet("/repositories/{id:guid}", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();
            var repository = await scope.Store.FindRepositoryAsync(id, ct);

            if (repository is null)
            {
                return ApiErrors.NotFound("repository");
            }

            return Results.Ok(Dtos.Repository(
                repository,
                ReviewerProfileOf(ReadConfig(runtime), repository.Slug.Value),
                await scope.Store.ListVerificationProfilesAsync(repository.Id, ct)));
        });
    }

    /// <summary>
    /// Which profile reviews a repository, read from the same configuration the orchestration
    /// side reads. Not recomputed and not cached: a stale answer here would misdescribe what
    /// the next run will actually do.
    /// </summary>
    internal static string? ReviewerProfileOf(Application.Configuration.HubConfig? config, string repositorySlug) =>
        config?.Organizations
            .SelectMany(o => o.Projects)
            .SelectMany(p => p.Repositories)
            .FirstOrDefault(r => string.Equals(r.Slug, repositorySlug, StringComparison.OrdinalIgnoreCase))
            ?.ReviewerProfile;

    internal static Application.Configuration.HubConfig? ReadConfig(HubRuntime runtime) =>
        File.Exists(runtime.ConfigPath) ? new YamlHubConfigReader().Read(runtime.ConfigPath) : null;
}
