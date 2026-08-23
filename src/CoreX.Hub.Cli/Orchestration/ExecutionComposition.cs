using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
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

        var orchestrator = new ExecutionOrchestrator(
            session.Store,
            new WorkspaceGateway(workspaceManager, _ => location),
            new ContextGateway(
                new ContextPackBuilder(new FileSystemGuardContextPathGuard()),
                contextRoot,
                _ => new ContextRepositoryFacts(repository.DefaultBranch, repository.ProtectedBranches)),
            new AgentGateway(BuildRuntimes(config, config.Workspace.Root)),
            verifier,
            new EvidenceGateway(evidenceStore),
            new ExecutionSafetyGates(new SecretGuard(new CatalogSecretScanner())),
            new LeaseWriter(session.OpenStore),
            new ProcessLivenessProbe(),
            clock,
            new ExecutionOrchestratorOptions());

        return new ExecutionComposition(orchestrator, verifier, repository.Id, config.Workspace.Root);
    }

    /// <summary>
    /// Builds the runtime registry from configuration.
    ///
    /// Phase 5 has exactly one kind of runtime and the configuration says so in as many
    /// words. There is no provider lookup here, no fallback to a second runtime when the
    /// first is unavailable, and no discovery: a profile that names a key nobody declared
    /// gets an honest "no runtime is registered under that key" from the gateway rather than
    /// something that happens to be lying around.
    /// </summary>
    private static IReadOnlyDictionary<string, IAgentRuntime> BuildRuntimes(HubConfig config, string workspaceRoot)
    {
        var runtimes = new Dictionary<string, IAgentRuntime>(StringComparer.OrdinalIgnoreCase);

        foreach (var declared in config.Agents.ScriptedRuntimes)
        {
            if (string.IsNullOrWhiteSpace(declared.Key))
            {
                throw new HubConfigurationException("a scripted runtime was declared without a key.");
            }

            // Written the way the fixture's own verbs are written - apply-patch, out-of-scope-write -
            // because that is what an operator reading either file would expect to match.
            var declaredMode = (declared.Mode ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);

            if (!Enum.TryParse<ScriptedAgentMode>(declaredMode, ignoreCase: true, out var mode))
            {
                throw new HubConfigurationException(
                    $"scripted runtime '{declared.Key}' declares mode '{declared.Mode}', which is not one of: "
                    + string.Join(", ", Enum.GetNames<ScriptedAgentMode>()));
            }

            var fixturePath = Path.GetFullPath(declared.FixturePath);

            if (!File.Exists(fixturePath))
            {
                throw new HubConfigurationException(
                    $"scripted runtime '{declared.Key}' names a fixture executable that does not exist: {fixturePath}");
            }

            runtimes[declared.Key] = new ScriptedAgentRuntime(
                new ScriptedAgentOptions { FixtureExecutablePath = fixturePath, Mode = mode },
                new ProcessAgentRunner(workspaceRoot));
        }

        return runtimes;
    }
}
