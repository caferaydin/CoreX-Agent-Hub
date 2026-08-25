using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Composition;

namespace CoreX.Hub.Cli.Commands;

/// <summary>Read-only catalog listings (Phase 1 CLI surface).</summary>
public static class CatalogCommands
{
    public static async Task<int> ListOrganizationsAsync(HubSession session, TextWriter output, CancellationToken ct)
    {
        var organizations = await session.Store.ListOrganizationsAsync(ct);

        var rows = organizations.Select(o => new[]
        {
            o.Slug.Value,
            o.Name,
            o.IsolationLevel.ToString(),
            o.RequiresHardIsolation ? "yes" : "no",
            o.Status.ToString(),
            o.FilesystemRoot,
        });

        Output.WriteTable(output, ["SLUG", "NAME", "ISOLATION", "REQ-HARD", "STATUS", "ROOT"], rows);

        foreach (var blocked in organizations.Where(o => !o.CanStartExecution))
        {
            await output.WriteLineAsync($"note: {blocked.Slug} is blocked: {blocked.BlockReason}");
        }

        return ExitCodes.Success;
    }

    public static async Task<int> ListProjectsAsync(
        HubSession session,
        string? organizationSlug,
        TextWriter output,
        CancellationToken ct)
    {
        var organizationId = await ResolveOrganizationIdAsync(session, organizationSlug, ct);
        var projects = await session.Store.ListProjectsAsync(organizationId, ct);
        var organizations = (await session.Store.ListOrganizationsAsync(ct)).ToDictionary(o => o.Id);

        var rows = projects.Select(p => new[]
        {
            organizations.TryGetValue(p.OrganizationId, out var org) ? org.Slug.Value : "?",
            p.Slug.Value,
            p.Name,
            p.TaskKeyPrefix,
        });

        Output.WriteTable(output, ["ORG", "SLUG", "NAME", "KEY-PREFIX"], rows);
        return ExitCodes.Success;
    }

    public static async Task<int> ListRepositoriesAsync(
        HubSession session,
        string? organizationSlug,
        string? projectSlug,
        TextWriter output,
        CancellationToken ct)
    {
        var organizationId = await ResolveOrganizationIdAsync(session, organizationSlug, ct);

        Guid? projectId = null;
        if (!string.IsNullOrWhiteSpace(projectSlug))
        {
            if (organizationId is not { } orgId)
            {
                throw new UsageException("--project requires --org.");
            }

            var project = await session.Store.FindProjectBySlugAsync(orgId, projectSlug, ct)
                ?? throw new UsageException($"Project '{projectSlug}' was not found.");
            projectId = project.Id;
        }

        var repositories = await session.Store.ListRepositoriesAsync(organizationId, projectId, ct);
        var projects = (await session.Store.ListProjectsAsync(organizationId, ct)).ToDictionary(p => p.Id);

        var rows = new List<string[]>();
        foreach (var repository in repositories)
        {
            var profile = await session.Store.FindDefaultVerificationProfileAsync(repository.Id, ct);
            rows.Add(
            [
                projects.TryGetValue(repository.ProjectId, out var project) ? project.Slug.Value : "?",
                repository.Slug.Value,
                repository.DefaultBranch,
                string.Join("|", repository.ProtectedBranches),
                profile is null ? "(none)" : profile.Name,
                repository.LocalPath,
            ]);
        }

        Output.WriteTable(output, ["PROJECT", "SLUG", "DEFAULT-BRANCH", "PROTECTED", "VERIFY-PROFILE", "PATH"], rows);
        return ExitCodes.Success;
    }

    private static async Task<Guid?> ResolveOrganizationIdAsync(
        HubSession session,
        string? organizationSlug,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return null;
        }

        var organization = await session.Store.FindOrganizationBySlugAsync(organizationSlug, ct)
            ?? throw new UsageException($"Organization '{organizationSlug}' was not found.");

        // Logical organization scoping (ADR-0012): from here on the session only sees
        // records owned by this organization.
        session.Scope.ScopeTo(organization.Id);
        return organization.Id;
    }
}
