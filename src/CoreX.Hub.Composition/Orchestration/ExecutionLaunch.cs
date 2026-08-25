using System.Runtime.Versioning;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Raised when the caller has to say which one, because more than one would do.
///
/// Its own type because the two adapters answer it differently: the CLI tells the operator
/// which flag to add, the API returns a 400 naming the candidates. Neither of them should
/// have to pattern-match on a message to tell this apart from "no such profile".
/// </summary>
public sealed class AmbiguousLaunchException : Exception
{
    public AmbiguousLaunchException(string message, IReadOnlyList<string> candidates)
        : base(message) => Candidates = candidates;

    public IReadOnlyList<string> Candidates { get; }
}

/// <summary>Everything one attempt needs, resolved and ready to run.</summary>
public sealed record PlannedExecution(
    HubTask Task,
    Organization Organization,
    Repository Repository,
    AgentProfile AgentProfile,
    ExecutionComposition Composition);

/// <summary>
/// Turning "run this task" into a composed execution.
///
/// This used to live inside the CLI command, which was fine while the CLI was the only way
/// to start one. It is here now because there are two callers, and two copies of "which
/// profile implements, which profile reviews, is this task even runnable" would be two
/// answers the day one of them was edited.
///
/// It resolves and composes. It deliberately does not run, does not claim the machine slot
/// and does not decide what a caller does with a refusal - those differ between a console
/// that exits and a host that has to answer a request and stay up.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExecutionLaunchPlanner
{
    public static async Task<PlannedExecution> PlanAsync(
        HubSession session,
        string taskKey,
        string? organizationSlug,
        string? requestedAgentProfile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var task = await ResolveTaskAsync(session, taskKey, organizationSlug, ct);

        if (task.State != TaskState.Ready)
        {
            throw new TaskOperationException(
                $"Task '{task.TaskKey}' is {task.State}, not Ready, so there is nothing to run.");
        }

        var organization = await session.Store.FindOrganizationAsync(task.OrganizationId, ct)
            ?? throw new TaskOperationException("The task's organization is no longer in the catalogue.");

        var repository = await session.Store.FindRepositoryAsync(task.RepositoryId, ct)
            ?? throw new TaskOperationException("The task's repository is no longer in the catalogue.");

        var profile = await ResolveAgentProfileAsync(session, task, requestedAgentProfile, ct);
        var config = new YamlHubConfigReader().Read(session.ConfigPath);
        var verificationProfile = await session.Store.FindDefaultVerificationProfileAsync(repository.Id, ct);
        var reviewer = await ResolveReviewerAsync(session, config, organization, repository, ct);

        var composition = ExecutionComposition.Create(
            session, config, organization, repository, verificationProfile, profile, reviewer, TimeProvider.System);

        return new PlannedExecution(task, organization, repository, profile, composition);
    }

    public static async Task<HubTask> ResolveTaskAsync(
        HubSession session,
        string taskKey,
        string? organizationSlug,
        CancellationToken ct)
    {
        if (!TaskKey.TryParse(taskKey, out var parsed))
        {
            throw new AmbiguousLaunchException(
                $"'{taskKey}' is not a valid task key. Expected <PROJECT>-<3 digit>, for example HUB-001.",
                []);
        }

        var matches = await session.Store.FindTasksByKeyAsync(parsed.Value, ct);

        if (!string.IsNullOrWhiteSpace(organizationSlug))
        {
            var organization = await session.Store.FindOrganizationBySlugAsync(organizationSlug, ct)
                ?? throw new TaskOperationException($"Organization '{organizationSlug}' was not found.");

            matches = [.. matches.Where(t => t.OrganizationId == organization.Id)];
        }

        return matches.Count switch
        {
            0 => throw new TaskOperationException($"Task '{parsed}' was not found."),
            1 => matches[0],
            _ => throw new TaskOperationException(
                $"Task '{parsed}' exists in more than one organization; name the organization to say which."),
        };
    }

    /// <summary>
    /// Which profile does the work.
    ///
    /// Only an Implementer is a candidate, whether it was asked for by name or picked
    /// because it was the only one. A reviewer profile is declared read only precisely so it
    /// cannot change anything, so running a task under one would produce an execution that
    /// could never write the result it was asked for - and the failure would look like the
    /// agent's rather than like the mistake it is.
    /// </summary>
    public static async Task<AgentProfile> ResolveAgentProfileAsync(
        HubSession session,
        HubTask task,
        string? requested,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var named = await session.Store.FindAgentProfileByNameAsync(task.OrganizationId, requested, ct)
                ?? throw new TaskOperationException($"Agent profile '{requested}' was not found.");

            return named.Role == AgentRole.Implementer
                ? named
                : throw new TaskOperationException(
                    $"Agent profile '{named.Name}' has the role {named.Role}; a task is run by an Implementer.");
        }

        var profiles = (await session.Store.ListAgentProfilesAsync(task.OrganizationId, ct))
            .Where(p => p.Role == AgentRole.Implementer)
            .ToList();

        return profiles.Count switch
        {
            0 => throw new TaskOperationException(
                "the task's organization declares no implementer agent profile, so there is nothing to run "
                + "it with."),
            1 => profiles[0],
            _ => throw new AmbiguousLaunchException(
                "the organization declares more than one implementer agent profile; name the one to use "
                + $"({string.Join(", ", profiles.Select(p => p.Name).Order())}).",
                [.. profiles.Select(p => p.Name).Order()]),
        };
    }

    /// <summary>
    /// Which profile reviews this repository, from the catalogue the repository points at.
    ///
    /// The name comes from configuration and the profile from the seeded catalogue, and the
    /// two are kept apart on purpose: a name with no profile behind it is a refusal that
    /// composition reports, not a repository that quietly stops being reviewed.
    /// </summary>
    public static async Task<ReviewerSelection> ResolveReviewerAsync(
        HubSession session,
        HubConfig config,
        Organization organization,
        Repository repository,
        CancellationToken ct)
    {
        var declared = config.Organizations
            .FirstOrDefault(o => string.Equals(o.Slug, organization.Slug.Value, StringComparison.OrdinalIgnoreCase))
            ?.Projects
            .SelectMany(project => project.Repositories)
            .FirstOrDefault(r => string.Equals(r.Slug, repository.Slug.Value, StringComparison.OrdinalIgnoreCase))
            ?.ReviewerProfile;

        if (string.IsNullOrWhiteSpace(declared))
        {
            return ReviewerSelection.None;
        }

        return new ReviewerSelection(
            declared,
            await session.Store.FindAgentProfileByNameAsync(organization.Id, declared, ct));
    }
}
