using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Cli.Agents;
using CoreX.Hub.Cli.Context;
using CoreX.Hub.Cli.Verification;
using CoreX.Hub.Cli.Workspace;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Cli.Orchestration;

/// <summary>
/// Wires one real execution: the same orchestrator, the same gateways and the same
/// verification engine the pipeline was proved with.
///
/// There is deliberately no second pipeline here. A command line specific "run" that
/// skipped the workspace, stubbed verification or ran the fixture directly would prove
/// nothing about the thing that ships, so this only assembles the parts and hands them to
/// <see cref="ExecutionOrchestrator"/>.
///
/// Everything it needs comes from two places that already exist: the catalog says which
/// repository, which organization and which verification profile, and the configuration
/// file says where the local toolchain and the local runtimes are. Nothing is discovered
/// on PATH and nothing reaches the network.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutionComposition
{
    private ExecutionComposition(
        ExecutionOrchestrator orchestrator,
        IExecutionVerifier verifier,
        Guid repositoryId,
        string workspaceRoot,
        bool reviewRequired)
    {
        Orchestrator = orchestrator;
        Verifier = verifier;
        RepositoryId = repositoryId;
        WorkspaceRoot = workspaceRoot;
        ReviewRequired = reviewRequired;
    }

    public ExecutionOrchestrator Orchestrator { get; }

    /// <summary>The same verifier the pipeline uses, for recovery to measure a commit again.</summary>
    public IExecutionVerifier Verifier { get; }

    public Guid RepositoryId { get; }

    public string WorkspaceRoot { get; }

    /// <summary>
    /// Whether this repository is reviewed at all, for the recovery path that cannot review.
    ///
    /// It is the composed answer rather than the configured name: by the time this is set,
    /// the profile has been found, its role and permission checked and its runtime proved
    /// able to do the job, so a true here means a review really was going to happen.
    /// </summary>
    public bool ReviewRequired { get; }

    /// <summary>
    /// Assembles everything one execution of one task needs, or explains what is missing.
    ///
    /// Failures here are configuration and catalog problems - no git, no runtime declared,
    /// no verification profile - and every one of them is reported as a refusal before any
    /// row is written. An execution that started and then discovered it had no way to
    /// verify itself would leave a worktree and a half finished attempt behind.
    /// </summary>
    public static ExecutionComposition Create(
        HubSession session,
        HubConfig config,
        Domain.Catalog.Organization organization,
        Domain.Catalog.Repository repository,
        VerificationProfile? verificationProfile,
        Domain.Catalog.AgentProfile? agentProfile,
        ReviewerSelection reviewer,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(repository);

        if (!GitToolchain.TryResolve(out var gitPath, out var diagnostic))
        {
            throw new HubConfigurationException(diagnostic);
        }

        var hubControlRoot = Path.Combine(session.DataDirectory, "git-control");
        var contextRoot = Path.Combine(session.DataDirectory, "context");
        Directory.CreateDirectory(hubControlRoot);
        Directory.CreateDirectory(contextRoot);

        var workspaceManager = new WorkspaceManager(
            new ProcessGitCommandExecutor(
                gitPath!,
                [organization.FilesystemRoot, config.Workspace.Root, hubControlRoot]),
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = hubControlRoot });

        var location = new RepositoryLocation(
            repository.LocalPath,
            organization.FilesystemRoot,
            config.Workspace.Root,
            organization.Slug.Value,
            repository.Slug.Value,
            repository.DefaultBranch);

        var allowlist = VerificationComposition.CreateAllowlist(config, out var allowlistProblems);

        if (allowlistProblems.Count > 0)
        {
            throw new HubConfigurationException(
                "the verification executable allowlist is not usable: " + string.Join("; ", allowlistProblems));
        }

        var evidenceStore = VerificationComposition.CreateEvidenceStore(session.DataDirectory);

        var environmentAllowlist = organization.EnvironmentAllowlist;

        var verifier = new VerificationGateway(
            workspaceManager,
            new VerificationBaselineService(
                VerificationComposition.CreateBaselineCache(session.DataDirectory), clock),
            root => new VerificationEngine(
                new ProcessVerificationCommandExecutor(root),
                new FileSystemGuardVerificationFileReader(),
                new BlobEvidenceSink(evidenceStore),
                allowlist,
                clock),
            _ => verificationProfile is null
                ? null
                : new VerificationSetup(
                    verificationProfile,
                    environmentAllowlist,
                    repository.LocalPath,
                    organization.FilesystemRoot,
                    config.Workspace.Root));

        // Runtimes are declared by configuration and named by the profile. Which one this
        // execution gets is settled here, before a row exists, so a key nobody declared is a
        // refusal rather than an execution that discovers it has nothing to run.
        var runtimes = AgentRuntimeRegistry.Build(
            config, config.Workspace.Root, workspaceManager.Hooks.EmptyGlobalConfigPath);

        if (agentProfile is not null)
        {
            AgentRuntimeRegistry.EnsureResolvable(runtimes, agentProfile);
        }

        // Review --------------------------------------------------------------------
        // Built here, before any row exists, for the same reason the implementer's runtime
        // is: a repository that says it is reviewed and cannot be is a configuration problem,
        // and finding it after a worktree and a result commit exist would mean an execution
        // ending as ReviewUnusable because of a typo in a file.
        var reviewEngine = BuildReviewer(
            session, config, organization, repository, reviewer, runtimes, workspaceManager, location, evidenceStore);

        var orchestrator = new ExecutionOrchestrator(
            session.Store,
            new WorkspaceGateway(workspaceManager, _ => location),
            new ContextGateway(
                new ContextPackBuilder(new FileSystemGuardContextPathGuard()),
                contextRoot,
                _ => new ContextRepositoryFacts(repository.DefaultBranch, repository.ProtectedBranches)),
            new AgentGateway(runtimes, environmentAllowlist),
            verifier,
            new EvidenceGateway(evidenceStore),
            new ExecutionSafetyGates(new SecretGuard(new CatalogSecretScanner())),
            new LeaseWriter(session.OpenStore),
            new DetachedStore(session.OpenStore),
            new ProcessLivenessProbe(),
            clock,
            new ExecutionOrchestratorOptions(),
            reviewEngine);

        return new ExecutionComposition(
            orchestrator, verifier, repository.Id, config.Workspace.Root, reviewEngine is not null);
    }

    /// <summary>
    /// Turns the reviewer a repository names into a reviewer that can actually run, or
    /// refuses and says why.
    ///
    /// Every check here is a way a review could look configured and be worthless. A profile
    /// with the wrong role would review with write permission; one whose runtime cannot take
    /// a schema would return prose that no validator could reject as prose; one with no model
    /// or effort would be reviewed by whatever the provider felt like today, which is not a
    /// thing anybody could reproduce. None of them is worth discovering at the review step.
    ///
    /// Naming no reviewer at all is not one of those problems. It is a repository saying it
    /// is not reviewed, and the pipeline records that as a skip with a reason.
    /// </summary>
    private static ReviewEngine? BuildReviewer(
        HubSession session,
        HubConfig config,
        Domain.Catalog.Organization organization,
        Domain.Catalog.Repository repository,
        ReviewerSelection reviewer,
        IReadOnlyDictionary<string, IAgentRuntime> runtimes,
        WorkspaceManager workspaceManager,
        RepositoryLocation location,
        EvidenceBlobStore evidenceStore)
    {
        if (reviewer.ProfileName is not { Length: > 0 } name)
        {
            return null;
        }

        if (reviewer.Profile is not { } profile)
        {
            throw new HubConfigurationException(
                $"repository '{repository.Slug}' names the reviewer profile '{name}', which organization "
                + $"'{organization.Slug}' does not declare.");
        }

        if (profile.Role != AgentRole.Reviewer)
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' has the role {profile.Role}; a review runs under a "
                + "Reviewer profile and under no other.");
        }

        if (profile.PermissionProfile != AgentPermissionProfile.ReadOnly)
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' requests {profile.PermissionProfile}; a reviewer that can "
                + "write is not an independent reviewer.");
        }

        // Stated rather than defaulted. A review is evidence, and evidence produced by an
        // unnamed model at an unnamed effort cannot be compared with the next one.
        if (string.IsNullOrWhiteSpace(profile.Model) || string.IsNullOrWhiteSpace(profile.Effort))
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' must name both a model and an effort, so that what "
                + "reviewed a result is recorded rather than assumed.");
        }

        AgentRuntimeRegistry.EnsureResolvable(runtimes, profile);

        var runtime = runtimes[profile.RuntimeKey];
        var capabilities = runtime.Describe();

        // ADR-0008: a runtime declares what it can do and is believed. Emulating either of
        // these would mean inventing a read only mode the provider does not have, or parsing
        // prose - and parsed prose is exactly what the schema exists to make unnecessary.
        if (!capabilities.SupportsReadOnlyMode)
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' names runtime '{profile.RuntimeKey}', which does not "
                + "declare a read only mode.");
        }

        if (!capabilities.SupportsStructuredSchema)
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' names runtime '{profile.RuntimeKey}', which cannot be "
                + "given an output schema, so its findings could not be validated.");
        }

        var reviewRoot = Path.Combine(session.DataDirectory, "review");
        var snapshotRoot = Path.Combine(config.Workspace.Root, "_review");

        return new ReviewEngine(
            new ReviewWorkspaceGateway(workspaceManager, location, snapshotRoot),
            new ReviewRuntimeGateway(runtime, profile, organization.EnvironmentAllowlist),
            new ReviewEvidenceReader(evidenceStore),
            new ReviewEngineOptions { ReviewerPromptFile = ReviewerPrompt.Materialise(reviewRoot) });
    }
}

/// <summary>
/// Which profile reviews this repository, resolved before composition runs.
///
/// Two fields rather than one, because "no reviewer is named" and "the named reviewer does
/// not exist" are different answers and only one of them is allowed to be silent.
/// </summary>
public sealed record ReviewerSelection(string? ProfileName, Domain.Catalog.AgentProfile? Profile)
{
    /// <summary>A repository that names no reviewer. Recorded as a skip, never as a pass.</summary>
    public static ReviewerSelection None { get; } = new(null, null);
}
