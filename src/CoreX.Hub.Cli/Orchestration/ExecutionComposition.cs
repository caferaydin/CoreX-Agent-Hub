using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Application.Orchestration;
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
        string workspaceRoot)
    {
        Orchestrator = orchestrator;
        Verifier = verifier;
        RepositoryId = repositoryId;
        WorkspaceRoot = workspaceRoot;
    }

    public ExecutionOrchestrator Orchestrator { get; }

    /// <summary>The same verifier the pipeline uses, for recovery to measure a commit again.</summary>
    public IExecutionVerifier Verifier { get; }

    public Guid RepositoryId { get; }

    public string WorkspaceRoot { get; }

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
        TimeProvider clock)
    {
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
            new ProcessLivenessProbe(),
            clock,
            new ExecutionOrchestratorOptions());

        return new ExecutionComposition(orchestrator, verifier, repository.Id, config.Workspace.Root);
    }
}
