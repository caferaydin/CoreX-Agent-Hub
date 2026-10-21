using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Composition.Context;
using CoreX.Hub.Composition.HardNetwork;
using CoreX.Hub.Composition.HardWorkspace;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Profiles;
using CoreX.Hub.Workspace;

namespace CoreX.Hub.Composition.Orchestration;

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
public sealed class ExecutionComposition : IAsyncDisposable
{
    /// <summary>
    /// How long cleanup may take after an execution has finished.
    ///
    /// Finite, because releasing a namespace holder must not be able to hang a command line for
    /// ever; and its own, because the execution's token is very often already cancelled by the
    /// time cleanup runs - which is exactly when the resources most need releasing.
    /// </summary>
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromMinutes(2);

    private readonly HardExecutionEnvironmentResolver? _hard;
    private readonly Func<IHubStore> _openStore;
    private bool _disposed;

    private ExecutionComposition(
        ExecutionOrchestrator orchestrator,
        IExecutionVerifier verifier,
        Guid repositoryId,
        string workspaceRoot,
        bool reviewRequired,
        bool isHard,
        HardExecutionEnvironmentResolver? hard,
        Func<IHubStore> openStore)
    {
        Orchestrator = orchestrator;
        Verifier = verifier;
        RepositoryId = repositoryId;
        WorkspaceRoot = workspaceRoot;
        ReviewRequired = reviewRequired;
        IsHard = isHard;
        _hard = hard;
        _openStore = openStore;
    }

    /// <summary>
    /// Whether this execution runs inside a hard boundary.
    ///
    /// Descriptive and decided once. Nothing reads this to choose a port - the ports were chosen
    /// together before this existed - and a caller that switched on it would be making a second
    /// routing decision beside the only one that is allowed to exist.
    /// </summary>
    public bool IsHard { get; }

    /// <summary>
    /// What cleanup could not release, when it could not.
    ///
    /// Recorded rather than thrown. Disposal runs at scope exit, very often while an execution
    /// failure is already propagating, and an exception from here would replace the reason the
    /// execution failed with the reason its cleanup failed - the less useful of the two by a wide
    /// margin. A caller that wants to surface this reads it; nothing depends on it having been
    /// read.
    /// </summary>
    public string? DisposalResidual { get; private set; }

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

        // Runtimes are declared by configuration and named by the profile. Which one this execution
        // gets is settled here, before a row exists, so a key nobody declared is a refusal rather
        // than an execution that discovers it has nothing to run.
        var runtimes = AgentRuntimeRegistry.Build(
            config, config.Workspace.Root, workspaceManager.Hooks.EmptyGlobalConfigPath);

        if (agentProfile is not null)
        {
            AgentRuntimeRegistry.EnsureResolvable(runtimes, agentProfile);
        }

        // Routing --------------------------------------------------------------------
        // One decision for the whole execution, taken here and never revisited. Asking each port to
        // read the organization's level for itself would let a configuration change between two of
        // those reads produce an execution whose agent was contained and whose verifier was not.
        var isolatedRuntimes = AgentRuntimeRegistry.IsolatedRuntimeKeys(config);

        var routing = RouteFor(
            session, config, organization, verificationProfile, agentProfile, runtimes,
            isolatedRuntimes, out var hardProfile);

        if (routing.IsRefused)
        {
            throw new HubConfigurationException(routing.Refusal!);
        }

        // Built only when it is the one that runs. A hard composition that also constructed the
        // Windows verifier would be a composition holding a host authority it must never reach for,
        // one edit away from reaching for it.
        VerificationGateway LogicalVerifier() => new(
            workspaceManager,
            new VerificationBaselineService(
                VerificationComposition.CreateBaselineCache(session.DataDirectory), clock),
            root => new VerificationEngine(
                new ProcessVerificationCommandExecutor(root),
                new FileSystemGuardVerificationFileReader(),
                new BlobEvidenceSink(evidenceStore),
                new HostVerificationExecutableResolver(allowlist),
                clock),
            _ => verificationProfile is null
                ? null
                : new VerificationSetup(
                    verificationProfile,
                    environmentAllowlist,
                    repository.LocalPath,
                    organization.FilesystemRoot,
                    config.Workspace.Root));

        // The hard side, built only for an organization that is one.
        //
        // Nothing here takes a live resource. The resolver adopts no identity, claims no attachment
        // and starts no distribution; it establishes only that a boundary could be resolved when an
        // admitted execution first asks for one.
        HardExecutionEnvironmentResolver? resolver = null;

        if (routing.IsHard)
        {
            var fabric = WslHardNetworkFabric.Create(session.DataDirectory);

            // Whether peer separation is a question this installation can even ask. With one hard
            // organization there is no neighbour to fail to reach, and refusing an execution for a
            // property it has no way to violate would be refusing it for arithmetic. The moment a
            // second one exists the check becomes required, and is proved behaviourally.
            var peers = config.Organizations
                .Count(o => o.HardIsolation is not null
                            && string.Equals(o.IsolationLevel, "Hard", StringComparison.OrdinalIgnoreCase));

            if (!HardExecutionEnvironmentResolver.TryCreate(
                    new HardNetworkConsumers(fabric, session.DataDirectory),
                    fabric,
                    () => peers > 1,
                    hardProfile!,
                    organization.Slug.Value,
                    session.DataDirectory,
                    out resolver,
                    out var resolverProblem))
            {
                throw new HubConfigurationException(
                    $"organization '{organization.Slug}' declares hard isolation and its environment "
                    + $"could not be reached: {resolverProblem}");
            }
        }

        // Review --------------------------------------------------------------------
        // Built here, before any row exists, for the same reason the implementer's runtime
        // is: a repository that says it is reviewed and cannot be is a configuration problem,
        // and finding it after a worktree and a result commit exist would mean an execution
        // ending as ReviewUnusable because of a typo in a file.
        var reviewEngine = BuildReviewer(
            session, config, organization, repository, reviewer, runtimes, workspaceManager, location,
            evidenceStore, resolver, isolatedRuntimes);

        // The five ports, chosen together or not at all.
        //
        // There is deliberately no way to assemble a mixed set. An agent perfectly contained whose
        // verification runs on Windows has simply moved the problem to the step that executes the
        // repository's own test suite, and a reviewer left outside has moved it one step further.
        var ports = resolver is null
            ? ExecutionPortSet.Logical(
                new WorkspaceGateway(workspaceManager, _ => location),
                new EvidenceGateway(evidenceStore),
                new AgentGateway(runtimes, environmentAllowlist),
                LogicalVerifier(),
                reviewEngine)
            : ExecutionPortSet.Hard(
                IsolationComposition.CreateWorkspace(
                    workspaceManager,
                    _ => location,
                    new ProcessGitCommandExecutor(
                        gitPath!, [organization.FilesystemRoot, config.Workspace.Root, hubControlRoot]),
                    hardProfile!,
                    session.DataDirectory,
                    resolver),
                IsolationComposition.CreateEvidence(hardProfile!, session.DataDirectory, resolver),
                new HardAgentGateway(runtimes, resolver),
                BuildHardVerifier(
                    session, organization, repository, config, verificationProfile, evidenceStore,
                    resolver, clock),
                reviewEngine);

        var orchestrator = new ExecutionOrchestrator(
            session.Store,
            ports.Workspace,
            new ContextGateway(
                new ContextPackBuilder(new FileSystemGuardContextPathGuard()),
                contextRoot,
                _ => new ContextRepositoryFacts(repository.DefaultBranch, repository.ProtectedBranches)),
            ports.Agent,
            ports.Verifier,
            ports.Evidence,
            new ExecutionSafetyGates(new SecretGuard(new CatalogSecretScanner())),
            new LeaseWriter(session.OpenStore),
            new DetachedStore(session.OpenStore),
            new ProcessLivenessProbe(),
            clock,
            new ExecutionOrchestratorOptions(),
            reviewEngine,

            // The gate on hard isolation. Built for every execution rather than only for the
            // organizations that need it, because an orchestrator constructed without one
            // refuses hard organizations outright, and an adapter that forgot to pass it would
            // look identical to one whose machine is not ready.
            IsolationComposition.Create(session.Store, config, session.DataDirectory));

        return new ExecutionComposition(
            orchestrator,
            ports.Verifier,
            repository.Id,
            config.Workspace.Root,
            reviewEngine is not null,
            ports.IsHard,
            resolver,
            session.OpenStore);
    }

    /// <summary>
    /// Decides, once, whether this execution runs inside a boundary - and refuses when a hard
    /// organization cannot.
    ///
    /// Everything consulted here is trusted state: the organization's declared level, the operator's
    /// configuration, this machine's plane and the selected runtime's own capability. No task
    /// prompt, repository file or provider output reaches it.
    ///
    /// The configuration completeness check is the one that saves the most. A hard organization
    /// whose verification profile names a tool its environment map does not is refused now, before a
    /// row exists - rather than after a workspace, an agent turn and a result commit, at the step
    /// that would have run the tool.
    /// </summary>
    private static ExecutionIsolationRouting RouteFor(
        HubSession session,
        HubConfig config,
        Domain.Catalog.Organization organization,
        VerificationProfile? verificationProfile,
        Domain.Catalog.AgentProfile? agentProfile,
        IReadOnlyDictionary<string, IAgentRuntime> runtimes,
        IReadOnlySet<string> isolatedRuntimes,
        out HardIsolationProfile? hardProfile)
    {
        hardProfile = null;

        if (organization.IsolationLevel != Domain.Catalog.IsolationLevel.Hard)
        {
            // The other half of the pairing rule. A runtime whose executable lives inside an
            // isolated environment has nothing to run on this machine, and letting a logical
            // organization name one would produce a launch failure at the agent step rather than a
            // configuration refusal now.
            return agentProfile is { } logicalProfile && isolatedRuntimes.Contains(logicalProfile.RuntimeKey)
                ? ExecutionIsolationRouting.Refuse(
                    $"agent profile '{logicalProfile.Name}' names runtime '{logicalProfile.RuntimeKey}', whose "
                    + $"fixture lives inside an isolated environment, but organization "
                    + $"'{organization.Slug}' is not hard isolated and has no such environment to run it in.")
                : ExecutionIsolationRouting.Logical;
        }

        if (!new ConfiguredHardIsolationProfiles(config)
                .TryGetProfile(organization.Slug.Value, out hardProfile, out var problem)
            || hardProfile is null)
        {
            return ExecutionIsolationRouting.Refuse(
                problem ?? "the organization declares hard isolation but supplies no hard configuration.");
        }

        var plane = IsolationComposition.CreatePlane(session.DataDirectory);

        var runtimeKey = agentProfile?.RuntimeKey ?? string.Empty;

        var routing = ExecutionIsolationRouting.For(
            organization.IsolationLevel,
            hardProfile,
            plane.IsAvailable,
            plane.UnavailableReason,
            runtimeKey.Length > 0 && runtimes.TryGetValue(runtimeKey, out var selected) ? selected : null,
            runtimeKey);

        if (routing.IsRefused)
        {
            return routing;
        }

        // Hard capable is not the same as hard placed. A runtime can implement the seam perfectly and
        // still name an executable that only exists on this machine, and handing that path to an
        // isolated workload would mean asking Linux to run a Windows path - which fails at exec time,
        // long after an execution row exists, and reads like a broken boundary rather than a
        // misconfigured runtime.
        if (agentProfile is { } profile && !isolatedRuntimes.Contains(profile.RuntimeKey))
        {
            return ExecutionIsolationRouting.Refuse(
                $"agent profile '{profile.Name}' names runtime '{profile.RuntimeKey}', whose executable is "
                + $"declared on this machine rather than inside organization '{organization.Slug}''s "
                + "environment. A hard organization runs what is in its own environment, and a host path "
                + "means nothing there.");
        }

        if (verificationProfile is null)
        {
            return routing;
        }

        var missing = HardVerificationExecutableResolver.MissingMappings(
            hardProfile, verificationProfile.Steps.Select(step => step.Executable));

        return missing.Count == 0
            ? routing
            : ExecutionIsolationRouting.Refuse(
                $"verification profile '{verificationProfile.Name}' names "
                + string.Join(", ", missing.Select(name => $"'{name}'"))
                + $", which organization '{organization.Slug}' does not map to a path inside its hard "
                + "environment; a hard organization never falls back to the host executable allowlist.");
    }

    /// <summary>
    /// The verifier for a hard organization: same engine, same parsers, same baselines, other side
    /// of the boundary.
    ///
    /// The engine is built per measurement rather than once, because it is bound to the tree being
    /// measured and to the execution whose namespace its commands belong in. Neither of those exists
    /// yet at composition time, which is exactly why this is a factory and not an instance.
    /// </summary>
    private static IExecutionVerifier BuildHardVerifier(
        HubSession session,
        Domain.Catalog.Organization organization,
        Domain.Catalog.Repository repository,
        HubConfig config,
        VerificationProfile? verificationProfile,
        EvidenceBlobStore evidenceStore,
        HardExecutionEnvironmentResolver resolver,
        TimeProvider clock)
    {
        var profile = resolver.Profile;

        if (!WslExecutionGateway.TryCreate(session.DataDirectory, out var gateway, out var problem))
        {
            throw new HubConfigurationException(
                $"organization '{organization.Slug}' declares hard isolation and its verification "
                + $"environment could not be reached: {problem}");
        }

        var control = new HardWorkspaceControl(gateway!, profile, HardWorkspaceLayout.For(profile.ExecutionRoot));

        return new HardVerificationGateway(
            new HardVerificationSnapshots(control),
            new VerificationBaselineService(
                VerificationComposition.CreateBaselineCache(session.DataDirectory), clock),
            (executionId, snapshotPath) => new VerificationEngine(
                // Repository-controlled commands, in this execution's own namespace and mount
                // sandbox. The Windows executor is not reachable from here at all.
                new HardVerificationCommandExecutor(
                    resolver.Bind(executionId), profile, snapshotPath, executionId),
                new HardVerificationFileReader(resolver.Guest),
                new BlobEvidenceSink(evidenceStore),
                new HardVerificationExecutableResolver(resolver.Guest, profile),
                clock),
            _ => verificationProfile is null
                ? null
                : new VerificationSetup(
                    verificationProfile,

                    // Nothing inherited. An isolated workload has no host parent worth taking names
                    // from, so the organization's host allowlist does not travel across.
                    [],
                    repository.LocalPath,
                    organization.FilesystemRoot,
                    config.Workspace.Root));
    }

    /// <summary>
    /// Releases what this execution took, once, and never what it was supposed to leave behind.
    ///
    /// Ephemeral isolation resources only: the network namespace holder, the attachment, the
    /// placement. The result commit, the evidence and any workspace the frozen failure policy says
    /// to preserve are execution artifacts rather than execution resources, and they are governed
    /// by the contracts that own them - disposing a composition is not deleting an execution.
    ///
    /// A logical execution, a refused one, and one whose environment was never needed all dispose
    /// to nothing, because nothing was ever taken.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hard is null)
        {
            return;
        }

        // The execution's own token is usually cancelled by now; cleanup gets a fresh, bounded one
        // so that stopping a run still releases what the run was holding.
        using var cleanup = new CancellationTokenSource(CleanupDeadline);

        try
        {
            await _hard.DisposeAsync();
        }
        catch (Exception ex)
        {
            DisposalResidual =
                "the hard isolation resources for this execution could not be fully released: "
                + ex.Message;
        }

        if (DisposalResidual is { Length: > 0 } residual)
        {
            await RecordResidualAsync(residual, cleanup.Token);
        }
    }

    /// <summary>
    /// Writes a cleanup residual onto the execution's own timeline.
    ///
    /// The same authority a snapshot residual already uses, for the same reason: something the Hub
    /// created outlived the run, an operator needs to know, and none of it is a reason to call the
    /// execution something other than what it was. So this records and never mutates - a passed
    /// execution whose veth would not go away is still a passed execution.
    ///
    /// It is written rather than left on a property because a property only exists for whoever
    /// remembers to read one. Every ownership path disposes; not every ownership path inspects.
    ///
    /// Best effort by construction. Disposal frequently runs while a failure is already on its way
    /// up, and a store that cannot be opened must not become the exception the caller sees instead
    /// of the one that actually ended the run.
    /// </summary>
    private async Task RecordResidualAsync(string residual, CancellationToken ct)
    {
        try
        {
            var executionId = _hard?.BoundExecutionId ?? Guid.Empty;

            if (executionId == Guid.Empty)
            {
                return;
            }

            var store = _openStore();

            var execution = await store.FindExecutionAsync(executionId, ct);

            if (execution is null)
            {
                return;
            }

            store.AddOrchestrationEvent(new Domain.Audit.OrchestrationEvent(
                Guid.CreateVersion7(),
                execution.OrganizationId,
                execution.Id,

                // Named for what it is, and distinct from the snapshot residual so the two cannot
                // be confused when somebody reads a timeline.
                "IsolationResidual",

                // Bounded, and operational only: what could not be released and why. No context
                // pack, no structured output, no evidence content.
                residual.Length <= 1000 ? residual : residual[..1000],
                DateTimeOffset.UtcNow));

            await store.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // The residual is already on DisposalResidual for anyone holding the composition. A
            // failure to record it is not worth replacing the caller's own failure with.
        }
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
        EvidenceBlobStore evidenceStore,
        HardExecutionEnvironmentResolver? hard,
        IReadOnlySet<string> isolatedRuntimes)
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

        // Whether this reviewer could work inside a boundary, asked here rather than at the review
        // step. Review reads the tree the agent just wrote, so for a hard organization it runs inside
        // the boundary or not at all - and discovering that after the agent has run and verification
        // has finished would mean paying for a whole execution to learn a configuration fact.
        if (hard is not null && !HardReviewerCompatibility.CanReviewInsideBoundary(runtime))
        {
            throw new HubConfigurationException(
                HardReviewerCompatibility.Refusal(profile.RuntimeKey, profile.Name));
        }

        // And placed inside it, not merely able to be. The reviewer runs the same way the implementer
        // does, so it is subject to the same pairing rule.
        if (hard is not null && !isolatedRuntimes.Contains(profile.RuntimeKey))
        {
            throw new HubConfigurationException(
                $"reviewer profile '{profile.Name}' names runtime '{profile.RuntimeKey}', whose executable is "
                + $"declared on this machine rather than inside organization '{organization.Slug}''s "
                + "environment; a hard organization's review runs inside the boundary or not at all.");
        }

        var reviewRoot = Path.Combine(session.DataDirectory, "review");
        var snapshotRoot = Path.Combine(config.Workspace.Root, "_review");

        // Created here, and not left to the first snapshot to create.
        //
        // A snapshot root is a trust root, and the path guard refuses one that does not
        // exist - it has nothing to canonicalise and cannot tell a real directory from a
        // reparse point that is not there yet. Every other Hub owned root on this path is
        // created the same way for the same reason.
        Directory.CreateDirectory(snapshotRoot);

        var options = new ReviewEngineOptions { ReviewerPromptFile = ReviewerPrompt.Materialise(reviewRoot) };

        if (hard is null)
        {
            return new ReviewEngine(
                new ReviewWorkspaceGateway(workspaceManager, location, snapshotRoot),
                new ReviewRuntimeGateway(runtime, profile, organization.EnvironmentAllowlist),
                new ReviewEvidenceReader(evidenceStore),
                options);
        }

        // Both halves of review move inside, not one. A reviewer contained in a namespace while its
        // tree was checked out on the host would leave the checkout - which is itself a classic place
        // repository controlled code gets to run - exactly where it was.
        if (!WslExecutionGateway.TryCreate(session.DataDirectory, out var gateway, out var problem))
        {
            throw new HubConfigurationException(
                $"organization '{organization.Slug}' declares hard isolation and its review environment "
                + $"could not be reached: {problem}");
        }

        var control = new HardWorkspaceControl(
            gateway!, hard.Profile, HardWorkspaceLayout.For(hard.Profile.ExecutionRoot));

        return new ReviewEngine(
            new HardReviewWorkspace(control, hard),
            new HardReviewRuntimeGateway(
                HardAgentRuntimeCompatibility.AsHardCapable(runtime)!, hard, hard.Profile, profile),
            new ReviewEvidenceReader(evidenceStore),
            options);
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
