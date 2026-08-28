using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// Runs a hard organization's agent turn inside the execution's boundary.
///
/// The same resolution the logical gateway does - the profile names a runtime key, the registry
/// answers - and then one extra question that has only two answers. A runtime either implements the
/// hard command seam or it does not, and one that does not is refused here rather than run on the
/// host, because a hard organization silently getting a logical run is the failure this whole phase
/// exists to prevent. The question is a type test rather than a provider name, so nothing here has
/// to be kept current as providers change.
///
/// The wrapped runtime's own <c>RunAsync</c> is never reached on this path. A vendor CLI adapter
/// launches its own host process inside that method; calling it would run repository-controlled work
/// on the developer's machine with every boundary switched off while every log line still said the
/// execution was hard isolated.
///
/// The context pack travels the trusted inward direction. The prompts the Hub generated for this
/// execution live on the host filesystem, which the workload cannot see, so they are placed inside
/// the environment as Hub owned, workload readable files. That is deliberately not the pinned source
/// transfer, whose meaning is committed git objects and only that.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardAgentGateway : IAgentRuntimeGateway
{
    /// <summary>The Hub's own instructions, inside the environment. Hub owned, agent readable.</summary>
    private const string SystemPromptLeaf = "system-prompt.md";

    private const string RulesPromptLeaf = "rules-prompt.md";

    private readonly IReadOnlyDictionary<string, IAgentRuntime> _runtimes;
    private readonly HardExecutionEnvironmentResolver _resolver;

    public HardAgentGateway(
        IReadOnlyDictionary<string, IAgentRuntime> runtimes,
        HardExecutionEnvironmentResolver resolver)
    {
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<AgentExecutionResult> RunAsync(
        AgentProfile profile,
        PreparedExecutionWorkspace workspace,
        BuiltContextPack context,
        HubTask task,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(task);

        // The correlation value the orchestrator passes is the execution's own id rendered as text,
        // and it is parsed back exactly rather than accepted as a name. Isolated resources are
        // recorded against this identity, so a value that merely looked like one would produce a
        // claim nothing could reconcile - exact parsing is what keeps "an arbitrary string" from
        // becoming resource authority.
        if (!Guid.TryParseExact(correlationId, "D", out var executionId) || executionId == Guid.Empty)
        {
            return Refused(
                "the agent could not be started because this execution has no usable identity, and an "
                + "isolated run must be recorded against one");
        }

        if (!_runtimes.TryGetValue(profile.RuntimeKey, out var runtime))
        {
            var known = _runtimes.Count == 0 ? "none are registered" : string.Join(", ", _runtimes.Keys.Order());

            return Refused($"no runtime is registered under '{profile.RuntimeKey}' (registered: {known})");
        }

        if (HardAgentRuntimeCompatibility.AsHardCapable(runtime) is not { } capable)
        {
            // Not a reason to run it on the host. An organization declared hard is an operator saying
            // the work must be contained, and running it uncontained because containment was
            // unavailable answers a question nobody asked.
            return Refused(
                $"the runtime '{profile.RuntimeKey}' cannot run inside a hard isolated environment, and a "
                + "hard organization does not fall back to running it on the host");
        }

        var environment = _resolver.Bind(executionId);

        var lease = await environment.LeaseAsync(ct);

        if (lease is null)
        {
            return Refused(
                "the hard isolation boundary for this execution is not available, so the agent was not "
                + "started: " + (environment.Refusal?.ToString() ?? "unknown"));
        }

        var executionRoot = environment.Profile.ExecutionRoot;
        var contextRoot = lease.ContextRoot(executionRoot);

        var systemPrompt = await PlaceAsync(lease, executionRoot, SystemPromptLeaf, context.SystemPromptPath, ct);

        if (systemPrompt.Path is null)
        {
            return Refused(systemPrompt.Problem!);
        }

        var rulesPrompt = await PlaceAsync(lease, executionRoot, RulesPromptLeaf, context.RulesPromptPath, ct);

        if (rulesPrompt.Path is null)
        {
            return Refused(rulesPrompt.Problem!);
        }

        var isolated = new HardAgentRuntime(
            capable, environment, environment.Profile, contextRoot, executionId);

        var request = new AgentRunRequest
        {
            // The Linux native worktree the hard workspace prepared. Never a host path: a working
            // directory this process can see is a working directory outside the boundary.
            WorkingDirectory = workspace.WorkspacePath,
            SystemPromptFile = systemPrompt.Path,
            AppendSystemPromptFile = rulesPrompt.Path,
            UserPrompt = task.Objective,
            PermissionProfile = profile.PermissionProfile,
            ModelHint = profile.Model,
            EffortHint = profile.Effort,

            // Deliberately empty. An allowlist selects from the parent's environment, and an isolated
            // workload has no parent worth inheriting from - what it gets is what a service of its
            // own unprivileged account gets, so there is no host value for a name to let through.
            EnvironmentAllowlist = [],
            SoftTimeout = TimeSpan.FromSeconds(profile.SoftTimeoutSeconds),
            IdleTimeout = TimeSpan.FromSeconds(profile.IdleTimeoutSeconds),
            HardTimeout = TimeSpan.FromSeconds(profile.HardTimeoutSeconds),
            CorrelationId = correlationId,
            SessionId = correlationId,
        };

        var result = await isolated.RunAsync(request, new Progress<AgentEvent>(_ => { }), ct);

        return new AgentExecutionResult(
            result.Outcome.ToString(),
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.StructuredOutput,
            result.Duration,
            result.Diagnostic);
    }

    /// <summary>
    /// Puts one Hub generated prompt inside the environment.
    ///
    /// Read from the host as bytes and written by the trusted service identity, which is what makes
    /// it readable by the workload and unwritable by it. An agent that could rewrite its own
    /// instructions and then be measured against them would be grading its own paper.
    /// </summary>
    private static async Task<(string? Path, string? Problem)> PlaceAsync(
        HardExecutionLease lease,
        string executionRoot,
        string leaf,
        string hostPath,
        CancellationToken ct)
    {
        ReadOnlyMemory<byte> content;

        try
        {
            content = await File.ReadAllBytesAsync(hostPath, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, $"the context pack file '{hostPath}' could not be read: {e.Message}");
        }

        var (path, refusal) = await lease.TransferContextAsync(executionRoot, leaf, content, ct);

        return path is null
            ? (null, $"'{leaf}' could not be placed inside the environment: {refusal}")
            : (path, null);
    }

    /// <summary>
    /// An agent that never ran, in the vocabulary the orchestrator already reads.
    ///
    /// Failed rather than anything softer, and never a run with no output: a boundary problem that
    /// read as an empty but successful turn would be recorded as the agent having nothing to say.
    /// </summary>
    private static AgentExecutionResult Refused(string diagnostic) =>
        new(
            nameof(AgentRunOutcome.Failed),
            null,
            string.Empty,
            string.Empty,
            null,
            TimeSpan.Zero,
            diagnostic);
}

/// <summary>
/// Whether a reviewer could review inside the boundary, asked before a repository is told it is
/// reviewed.
///
/// Hoisted out of the review adapters so admission can ask it without building one. Discovering
/// that a reviewer cannot be isolated only after the agent has run and verification has finished
/// would mean paying for a whole execution to learn a configuration fact.
/// </summary>
public static class HardReviewerCompatibility
{
    public static bool CanReviewInsideBoundary(IAgentRuntime runtime) =>
        HardAgentRuntimeCompatibility.IsHardCapable(runtime);

    /// <summary>The refusal, worded for an operator who has to change something.</summary>
    public static string Refusal(string runtimeKey, string profileName) =>
        $"reviewer profile '{profileName}' names runtime '{runtimeKey}', which cannot run inside a hard "
        + "isolated environment. A hard organization's review reads the tree the agent just wrote, so it "
        + "runs inside the boundary or not at all; it is never moved to the host to make it possible.";

    /// <summary>A deterministic identity for one review turn, from the execution and the attempt.</summary>
    internal static string Session(Guid executionId, int attempt) =>
        executionId.ToString("N", CultureInfo.InvariantCulture)
        + "-review-" + attempt.ToString(CultureInfo.InvariantCulture);

    internal static ReadOnlyMemory<byte> Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
