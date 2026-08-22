using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Application.Catalog;

/// <summary>
/// Imports the catalog from <c>hub.config.yaml</c>.
///
/// Policy (architecture section 21):
///   * additive upsert by natural key,
///   * NEVER deletes persisted records,
///   * on conflict, configuration wins for descriptive fields; identity is immutable,
///   * deterministic: identical config seeded into two fresh databases yields identical rows,
///   * idempotent: a second run creates and updates nothing, and writes no audit row.
/// </summary>
public sealed class CatalogSeeder(IHubStore store)
{
    private readonly IHubStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SeedReport> SeedAsync(HubConfig config, DateTimeOffset nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.SchemaVersion != 1)
        {
            throw new HubConfigurationException(
                $"Unsupported hub.config schemaVersion {config.SchemaVersion}; this build understands version 1.");
        }

        if (string.IsNullOrWhiteSpace(config.Workspace.Root))
        {
            throw new HubConfigurationException("workspace.root must be set (ADR-0004).");
        }

        if (config.Organizations.Count == 0)
        {
            throw new HubConfigurationException("hub.config.yaml declares no organizations.");
        }

        var counters = new Dictionary<string, Counter>(StringComparer.Ordinal)
        {
            ["Organization"] = new(),
            ["Project"] = new(),
            ["Repository"] = new(),
            ["VerificationProfile"] = new(),
            ["AgentProfile"] = new(),
        };

        var seenOrganizationSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var orgConfig in config.Organizations)
        {
            var organization = await UpsertOrganizationAsync(orgConfig, counters["Organization"], nowUtc, ct);
            seenOrganizationSlugs.Add(organization.Slug.Value);

            foreach (var agentConfig in orgConfig.AgentProfiles)
            {
                await UpsertAgentProfileAsync(organization, agentConfig, counters["AgentProfile"], nowUtc, ct);
            }

            foreach (var projectConfig in orgConfig.Projects)
            {
                var project = await UpsertProjectAsync(organization, projectConfig, counters["Project"], nowUtc, ct);

                foreach (var repoConfig in projectConfig.Repositories)
                {
                    var repository = await UpsertRepositoryAsync(
                        organization, project, repoConfig, counters["Repository"], nowUtc, ct);

                    foreach (var profileConfig in repoConfig.VerificationProfiles)
                    {
                        await UpsertVerificationProfileAsync(
                            organization, repository, profileConfig, counters["VerificationProfile"], nowUtc, ct);
                    }
                }
            }
        }

        var orphans = await FindOrphansAsync(seenOrganizationSlugs, ct);

        var report = new SeedReport(
            [.. counters.Select(kv => new SeedCounts(kv.Key, kv.Value.Created, kv.Value.Updated, kv.Value.Unchanged))],
            orphans);

        if (!report.MadeNoChanges)
        {
            _store.AddAuditEvent(new AuditEvent(
                Guid.CreateVersion7(),
                organizationId: null,
                eventType: "CatalogSeeded",
                actor: "system",
                summary: $"Catalog seed applied: {report.TotalCreated} created, {report.TotalUpdated} updated.",
                taskId: null,
                executionId: null,
                correlationId: null,
                occurredAtUtc: nowUtc));
        }

        await _store.SaveChangesAsync(ct);
        return report;
    }

    private async Task<Organization> UpsertOrganizationAsync(
        OrganizationConfig config,
        Counter counter,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var slug = ParseSlug(config.Slug, "organizations[].slug");
        var isolationLevel = ParseEnum<IsolationLevel>(config.IsolationLevel, $"organization '{slug}' isolationLevel");

        var existing = await _store.FindOrganizationBySlugAsync(slug.Value, ct);
        if (existing is null)
        {
            var created = new Organization(
                DeterministicGuid.Create("organization", slug.Value),
                slug,
                config.Name,
                config.FilesystemRoot,
                isolationLevel,
                config.RequiresHardIsolation,
                config.EnvironmentAllowlist,
                nowUtc);

            _store.AddOrganization(created);
            counter.Created++;
            return created;
        }

        var changed = existing.Update(
            config.Name,
            config.FilesystemRoot,
            isolationLevel,
            config.RequiresHardIsolation,
            config.EnvironmentAllowlist,
            nowUtc);

        counter.Record(changed);
        return existing;
    }

    private async Task<Project> UpsertProjectAsync(
        Organization organization,
        ProjectConfig config,
        Counter counter,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var slug = ParseSlug(config.Slug, $"organization '{organization.Slug}' projects[].slug");

        var existing = await _store.FindProjectBySlugAsync(organization.Id, slug.Value, ct);
        if (existing is null)
        {
            var created = new Project(
                DeterministicGuid.Create("project", organization.Slug.Value, slug.Value),
                organization.Id,
                slug,
                config.Name,
                config.TaskKeyPrefix,
                nowUtc);

            _store.AddProject(created);
            counter.Created++;
            return created;
        }

        counter.Record(existing.Update(config.Name, config.TaskKeyPrefix, nowUtc));
        return existing;
    }

    private async Task<Domain.Catalog.Repository> UpsertRepositoryAsync(
        Organization organization,
        Project project,
        RepositoryConfig config,
        Counter counter,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var slug = ParseSlug(config.Slug, $"project '{project.Slug}' repositories[].slug");

        var existing = await _store.FindRepositoryBySlugAsync(project.Id, slug.Value, ct);
        if (existing is null)
        {
            var created = new Domain.Catalog.Repository(
                DeterministicGuid.Create("repository", organization.Slug.Value, project.Slug.Value, slug.Value),
                organization.Id,
                project.Id,
                slug,
                config.Name,
                config.LocalPath,
                config.DefaultBranch,
                config.ProtectedBranches,
                nowUtc);

            _store.AddRepository(created);
            counter.Created++;
            return created;
        }

        counter.Record(existing.Update(
            config.Name,
            config.LocalPath,
            config.DefaultBranch,
            config.ProtectedBranches,
            nowUtc));

        return existing;
    }

    private async Task UpsertVerificationProfileAsync(
        Organization organization,
        Domain.Catalog.Repository repository,
        VerificationProfileConfig config,
        Counter counter,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var name = Guard.NotBlankMax(config.Name, "verificationProfiles[].name", 100);
        var steps = config.Steps.Select(BuildStep).ToList();

        var existing = (await _store.ListVerificationProfilesAsync(repository.Id, ct))
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (existing is null)
        {
            var created = new VerificationProfile(
                DeterministicGuid.Create(
                    "verificationProfile", organization.Slug.Value, repository.Slug.Value, name),
                organization.Id,
                repository.Id,
                name,
                config.IsDefault,
                config.WorkingDirectory,
                steps,
                nowUtc);

            _store.AddVerificationProfile(created);
            counter.Created++;
            return;
        }

        counter.Record(existing.Update(config.IsDefault, config.WorkingDirectory, steps, nowUtc));
    }

    private async Task UpsertAgentProfileAsync(
        Organization organization,
        AgentProfileConfig config,
        Counter counter,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var name = Guard.NotBlankMax(config.Name, "agentProfiles[].name", 100);
        var role = ParseEnum<AgentRole>(config.Role, $"agent profile '{name}' role");
        var permission = ParseEnum<AgentPermissionProfile>(
            config.PermissionProfile, $"agent profile '{name}' permissionProfile");

        var existing = await _store.FindAgentProfileByNameAsync(organization.Id, name, ct);
        if (existing is null)
        {
            var created = new AgentProfile(
                DeterministicGuid.Create("agentProfile", organization.Slug.Value, name),
                organization.Id,
                name,
                role,
                config.Provider,
                config.RuntimeKey,
                permission,
                config.Model,
                config.Effort,
                config.SoftTimeoutSeconds,
                config.IdleTimeoutSeconds,
                config.HardTimeoutSeconds,
                nowUtc);

            _store.AddAgentProfile(created);
            counter.Created++;
            return;
        }

        counter.Record(existing.Update(
            role,
            config.Provider,
            config.RuntimeKey,
            permission,
            config.Model,
            config.Effort,
            config.SoftTimeoutSeconds,
            config.IdleTimeoutSeconds,
            config.HardTimeoutSeconds,
            nowUtc));
    }

    private async Task<IReadOnlyList<SeedOrphan>> FindOrphansAsync(
        HashSet<string> configuredOrganizationSlugs,
        CancellationToken ct)
    {
        var orphans = new List<SeedOrphan>();

        foreach (var organization in await _store.ListOrganizationsAsync(ct))
        {
            if (!configuredOrganizationSlugs.Contains(organization.Slug.Value))
            {
                orphans.Add(new SeedOrphan("Organization", organization.Slug.Value));
            }
        }

        return orphans;
    }

    private static VerificationStep BuildStep(VerificationStepConfig config)
    {
        var parser = ParseEnum<VerificationResultParser>(
            config.ResultParser, $"verification step '{config.Name}' resultParser");

        return new VerificationStep(
            config.Name,
            config.Executable,
            config.Args,
            config.TimeoutSeconds,
            config.Required,
            parser);
    }

    private static Slug ParseSlug(string? value, string field)
    {
        if (!Slug.TryParse(value, out var slug))
        {
            throw new HubConfigurationException($"{field}: '{value}' is not a valid slug.");
        }

        return slug;
    }

    private static TEnum ParseEnum<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            var allowed = string.Join(", ", Enum.GetNames<TEnum>());
            throw new HubConfigurationException($"{field}: '{value}' is not valid. Expected one of: {allowed}.");
        }

        return parsed;
    }

    private sealed class Counter
    {
        public int Created { get; set; }

        public int Updated { get; set; }

        public int Unchanged { get; set; }

        public void Record(bool changed)
        {
            if (changed)
            {
                Updated++;
            }
            else
            {
                Unchanged++;
            }
        }
    }
}
