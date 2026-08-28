using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>Why a lease could not be acquired, in words an operator can act on.</summary>
public sealed record HardLeaseRefusal(string Stage, string Detail)
{
    public override string ToString() => $"{Stage}: {Detail}";
}

/// <summary>
/// One execution's hold on one hard isolated environment.
///
/// It exists because the boundary is not a moment, it is a duration. Preparing a namespace,
/// running the agent inside it and then letting it go before the repository's own test suite runs
/// would be a boundary that covers the part of the execution least likely to be hostile: the agent
/// is a program the Hub launched, while the tests are a program the repository wrote. So one
/// execution acquires one environment, and every untrusted process in that execution - the agent,
/// the verification commands, and review whenever review runs repository state - is placed in it
/// by the same authority.
///
/// That is the whole reason this type holds a single <see cref="HardWorkloadPlacement"/> rather
/// than a way to make one. A second placement computed later would be a second namespace: the
/// agent contained in the organization's, the verifier somewhere else, and every test still green
/// because each one only ever checked its own half.
///
/// Resources only. The lease knows nothing about PreparingWorkspace, RunningAgent or Completed,
/// and cannot move an execution between them - the orchestrator remains the single lifecycle
/// authority, and this hands it the environment to run in.
///
/// Acquisition is fail closed at every stage, and unwinds only what it actually took. A network
/// attachment that was never established is not released, and a workspace the frozen policy says
/// to preserve for diagnosis is not deleted because a later stage failed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionLease : IAsyncDisposable
{
    private readonly HardNetworkConsumers _consumers;
    private readonly HardIsolationProfile _profile;
    private readonly string _organizationSlug;
    private readonly TrustedGuestFileTransfer _transfer;
    private bool _attachmentHeld;
    private bool _released;

    private HardExecutionLease(
        HardNetworkConsumers consumers,
        HardIsolationProfile profile,
        string organizationSlug,
        HardNetworkAttachment attachment,
        HardWorkloadPlacement placement,
        IHardProcessExecutor executor,
        TrustedGuestFileTransfer transfer,
        Guid executionId)
    {
        _consumers = consumers;
        _profile = profile;
        _organizationSlug = organizationSlug;
        _transfer = transfer;
        _attachmentHeld = true;

        Attachment = attachment;
        Placement = placement;
        Executor = executor;
        ExecutionId = executionId;
    }

    /// <summary>This organization's live attachment to the trusted plane.</summary>
    public HardNetworkAttachment Attachment { get; }

    /// <summary>
    /// The one place every untrusted process in this execution runs.
    ///
    /// Read by the agent adapter, the verification adapter and isolated review. None of them can
    /// construct one, which is what makes "they all ran in the same namespace" a fact rather than
    /// a convention.
    /// </summary>
    public HardWorkloadPlacement Placement { get; }

    /// <summary>An executor already bound to <see cref="Placement"/>.</summary>
    public IHardProcessExecutor Executor { get; }

    /// <summary>The trusted channel into the environment, for readers this execution owns.</summary>
    internal TrustedGuestFileTransfer Guest => _transfer;

    public Guid ExecutionId { get; }

    public HardIsolationProfile Profile => _profile;

    /// <summary>
    /// Acquires an environment for one execution, or explains which stage refused.
    ///
    /// The order is the security property. Nothing untrusted can start until every boundary this
    /// execution will rely on is established and measured, so there is no window in which a
    /// workload exists and a boundary does not.
    /// </summary>
    internal static async Task<(HardExecutionLease? Lease, HardLeaseRefusal? Refusal)> AcquireAsync(
        HardNetworkConsumers consumers,
        IHardEnvironmentChannel channel,
        HardIsolationProfile profile,
        string organizationSlug,
        Guid executionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(profile);

        // Nothing below can belong to an execution with no identity. The claim written on disk is
        // named by this value, and a claim nobody can name is one reconciliation cannot classify
        // after a crash.
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);

        // The trusted channel into the environment, opened here rather than accepted, so a lease
        // cannot be handed a gateway pointing at some other machine's distribution.
        if (!channel.TryOpen(out var gateway, out var unreachable))
        {
            return (null, new HardLeaseRefusal("environment", unreachable ?? "WSL cannot be reached."));
        }

        // This execution's share of the organization's attachment, claimed through the ownership
        // layer rather than by preparing the physical fabric directly.
        //
        // The distinction is the whole reason that layer exists. An attachment is per organization,
        // so two executions of one organization use the same one; a lease that prepared and released
        // the fabric itself would have the first of them to finish tear the namespace out from under
        // the second. That fails closed for the peer and breaks it just the same. What this holds is
        // a durable claim, and the physical plane comes up for the first claim and goes away after
        // the last.
        var attachment = await consumers.AcquireAsync(profile, organizationSlug, executionId, ct);

        if (attachment is null)
        {
            return (null, new HardLeaseRefusal(
                "network",
                $"organization '{organizationSlug}' has no live network attachment, so a workload "
                + "would run in the machine's shared namespace."));
        }

        var placement = new HardWorkloadPlacement(attachment.NamespaceReference);

        if (!placement.IsUsable)
        {
            await ReleaseAttachmentAsync(consumers, profile, organizationSlug, executionId);

            return (null, new HardLeaseRefusal(
                "placement",
                $"'{attachment.NamespaceReference}' is not a usable namespace reference."));
        }

        // Every property that will contain the workload, rendered and checked before anything is
        // launched. A boundary that cannot even be expressed is not one to discover at exec time.
        var properties = HardWorkloadLaunch.Properties(placement);

        if (properties.Count == 0)
        {
            await ReleaseAttachmentAsync(consumers, profile, organizationSlug, executionId);

            return (null, new HardLeaseRefusal("placement", "the workload boundary rendered nothing."));
        }

        var executor = channel.PlacedExecutor(placement);

        return (
            new HardExecutionLease(
                consumers,
                profile,
                organizationSlug,
                attachment,
                placement,
                executor,
                new TrustedGuestFileTransfer(gateway!, profile),
                executionId),
            null);
    }

    /// <summary>
    /// Puts the Hub's own context pack inside the environment, where the workload can read it.
    ///
    /// Trusted input, and named as such. This is not the pinned source transfer and must never
    /// become it: the source path carries committed git objects and nothing else, which is what
    /// keeps a host's uncommitted work out of the environment. What travels here is bytes the Hub
    /// generated for this execution.
    /// </summary>
    public async Task<(string? Path, HardLeaseRefusal? Refusal)> TransferContextAsync(
        string executionRoot,
        string leaf,
        ReadOnlyMemory<byte> content,
        CancellationToken ct)
    {
        var written = await _transfer.WriteAsync(ContextRoot(executionRoot), leaf, content, ct);

        return written.Ok
            ? (written.Detail, null)
            : (null, new HardLeaseRefusal("context", written.Detail));
    }

    /// <summary>
    /// Reads a bounded result the execution produced, through the trusted service identity.
    ///
    /// Refusal and absence are different answers and stay different. A file that is not there
    /// means no structured result was produced, which every runtime already understands; a file
    /// too large to accept is a failure, and reporting it as absence would be the Hub deciding
    /// that an oversized result is the same as no result.
    /// </summary>
    public Task<TrustedReadResult> ReadStructuredOutputAsync(
        string executionRoot, string path, CancellationToken ct) =>
        _transfer.ReadAsync(executionRoot, path, ct);

    /// <summary>Where this execution's trusted Hub inputs live, beneath the execution root.</summary>
    public string ContextRoot(string executionRoot) =>
        executionRoot.TrimEnd('/') + "/_corex-context/"
        + ExecutionId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gives up this execution's claim, and nothing else.
    ///
    /// One claim, not the organization's attachment. Whether the physical plane goes away is the
    /// ownership layer's decision and depends on whether anybody else still holds a claim, which is
    /// exactly the judgement a single execution is not in a position to make.
    ///
    /// Only the claim: the workspace, the evidence and the result commit outlive an execution by
    /// design, and their retention is decided by the frozen policies that own them rather than by
    /// whatever happened to fail here.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_released || !_attachmentHeld)
        {
            return;
        }

        _released = true;

        await _consumers.ReleaseAsync(_profile, _organizationSlug, ExecutionId, CancellationToken.None);
    }

    private static async Task ReleaseAttachmentAsync(
        HardNetworkConsumers consumers,
        HardIsolationProfile profile,
        string organizationSlug,
        Guid executionId)
    {
        try
        {
            await consumers.ReleaseAsync(profile, organizationSlug, executionId, CancellationToken.None);
        }
        catch (Exception)
        {
            // A failed unwind must not replace the refusal that caused it. The caller is being
            // told why no workload started, which is the more useful of the two facts.
        }
    }
}
