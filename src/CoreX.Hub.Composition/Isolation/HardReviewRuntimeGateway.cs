using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Runs a reviewer inside the same boundary the execution it is reviewing ran in.
///
/// Review was classified as repository-backed rather than trusted: it opens a worktree at the
/// result commit and runs a process with that tree as its working directory. That makes the
/// reviewer a process operating on content the agent just wrote, which is exactly the shape the
/// hard boundary exists for. Running it on the host would reopen the co-tenancy threat one step
/// after verification closed it, so this exists and the Windows review runtime gateway is not used
/// for a hard organization at all.
///
/// It borrows everything and owns nothing. The placement, the executor and the trusted transfer
/// all come from the execution's lease, so the reviewer lands in the same network namespace as the
/// agent and the verification commands rather than in one it worked out for itself. It does not
/// dispose the environment: review is the last thing to run, and an adapter that tidied up after
/// itself here would be releasing a boundary the orchestrator has not finished with.
///
/// The reviewer's prompt is Hub-generated trusted input and travels the same way a context pack
/// does. Its structured answer comes back through the bounded readback, which is what keeps a
/// reviewer from handing the Hub an unbounded document from inside the environment.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardReviewRuntimeGateway : IReviewRuntimeGateway
{
    /// <summary>The reviewer's own prompt, inside the environment. Hub owned, agent readable.</summary>
    private const string PromptLeaf = "reviewer-prompt.md";

    /// <summary>Where the reviewer is asked to write its document. Hub chosen, never provider chosen.</summary>
    private const string ResultLeaf = "review-result.json";

    private readonly IHardCapableAgentRuntime _capable;
    private readonly HardExecutionEnvironmentResolver _resolver;
    private readonly HardIsolationProfile _profile;
    private readonly AgentProfile _reviewerProfile;

    /// <summary>
    /// Built before any execution exists, and bound to one when a review actually runs.
    ///
    /// The resolver rather than an environment, because a review engine is composed at the same
    /// moment the implementer's ports are - before there is a row to belong to. Taking a ready-made
    /// environment here would mean either constructing one too early or letting review acquire its
    /// own, and a reviewer isolated in a namespace of its own is a reviewer that is not looking at
    /// the execution it is reviewing.
    /// </summary>
    public HardReviewRuntimeGateway(
        IHardCapableAgentRuntime capable,
        HardExecutionEnvironmentResolver resolver,
        HardIsolationProfile profile,
        AgentProfile reviewerProfile)
    {
        _capable = capable ?? throw new ArgumentNullException(nameof(capable));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _reviewerProfile = reviewerProfile ?? throw new ArgumentNullException(nameof(reviewerProfile));
    }

    /// <summary>
    /// Whether a runtime could review inside the boundary at all.
    ///
    /// Asked during composition, before a repository is told it is reviewed. A reviewer that
    /// cannot be placed inside the boundary makes hard review unsupported for that configuration,
    /// which is a refusal rather than a reason to review on the host.
    /// </summary>
    public static bool IsHardCapable(IAgentRuntime runtime) =>
        HardAgentRuntimeCompatibility.IsHardCapable(runtime);

    public async Task<ReviewRuntimeResult> RunAsync(ReviewRuntimeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same environment the agent and the verification commands ran in, resolved from the
        // execution's own identity rather than from the session string the reviewer is told.
        var environment = _resolver.Bind(request.ExecutionId);

        var lease = await environment.LeaseAsync(ct);

        if (lease is null)
        {
            return Refused(
                "the hard isolation boundary for this execution is not available, so the reviewer was "
                + "not started: " + (environment.Refusal?.ToString() ?? "unknown"));
        }

        var root = ReviewRoot(request.ExecutionId);

        // The reviewer's instructions are the Hub's, and they go in the way trusted input goes.
        // Read from the host file the review engine materialised, written into a Hub owned
        // directory the reviewer can read and cannot write.
        var prompt = await ReadHostPromptAsync(request.SystemPromptFile, ct);

        if (prompt is null)
        {
            return Refused($"the reviewer prompt at '{request.SystemPromptFile}' could not be read.");
        }

        var (promptPath, promptRefusal) = await lease.TransferContextAsync(root, PromptLeaf, prompt.Value, ct);

        if (promptPath is null)
        {
            return Refused("the reviewer prompt could not be placed inside the environment: " + promptRefusal);
        }

        // The reviewer writes its document where the Hub says, inside the execution's own area.
        // A path the runtime chose would be a path the Hub then has to trust enough to read.
        var resultPath = root + "/" + ResultLeaf;

        var runtime = new HardAgentRuntime(_capable, environment, _profile, root, request.ExecutionId);

        var run = await runtime.RunAsync(
            new AgentRunRequest
            {
                // The Linux snapshot the hard review workspace prepared, at the exact result commit.
                WorkingDirectory = request.WorkingDirectory,
                SystemPromptFile = promptPath,
                UserPrompt = request.UserPrompt,

                // Declared read only by the profile, and checked before composition allowed this
                // reviewer at all. Nothing here chooses tools.
                PermissionProfile = _reviewerProfile.PermissionProfile,
                ModelHint = _reviewerProfile.Model,
                EffortHint = _reviewerProfile.Effort,
                StructuredOutputSchema = request.StructuredOutputSchema,
                StructuredOutputPath = resultPath,
                SoftTimeout = TimeSpan.FromSeconds(_reviewerProfile.SoftTimeoutSeconds),
                IdleTimeout = TimeSpan.FromSeconds(_reviewerProfile.IdleTimeoutSeconds),
                HardTimeout = TimeSpan.FromSeconds(_reviewerProfile.HardTimeoutSeconds),
                CorrelationId = request.SessionId,
                SessionId = request.SessionId,
            },
            new Progress<AgentEvent>(_ => { }),
            ct);

        return new ReviewRuntimeResult
        {
            Outcome = run.Outcome.ToString(),
            StructuredOutput = run.StructuredOutput,
            Stdout = run.Stdout,
            Stderr = run.Stderr,
            ProviderSessionId = run.ProviderSessionId,
            Diagnostic = run.Diagnostic,
        };
    }

    /// <summary>This execution's own review area, beneath the execution root the profile names.</summary>
    private string ReviewRoot(Guid executionId) =>
        _profile.ExecutionRoot.TrimEnd('/') + "/_corex-review/"
        + executionId.ToString("N", CultureInfo.InvariantCulture);

    private static async Task<ReadOnlyMemory<byte>?> ReadHostPromptAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// A reviewer that never ran, described in the runtime's own vocabulary.
    ///
    /// Failed rather than anything softer, and never a review with no findings: the engine treats
    /// an unusable reviewer as unusable, which is what stops a boundary problem from being read as
    /// a clean review.
    /// </summary>
    private static ReviewRuntimeResult Refused(string diagnostic) => new()
    {
        Outcome = nameof(AgentRunOutcome.Failed),
        Diagnostic = diagnostic,
    };
}
