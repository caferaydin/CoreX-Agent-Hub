using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Saying which execution a hard port is acting for, without saying anything about isolation.
///
/// The narrow half of the resolver, for the ports that need the routing identity to be checked but
/// have no use for a live environment. Writing an evidence blob is the case that matters: it goes to
/// the organization's isolated store through the trusted service identity and needs no network
/// namespace at all, so acquiring a boundary in order to write one would be taking a live resource
/// to satisfy a bookkeeping rule.
///
/// What it still buys is the rule itself. Every hard port in one composition binds, so two ports
/// disagreeing about which execution they belong to is caught the first time it happens rather than
/// discovered in a store that holds one execution's evidence under another's identity.
/// </summary>
internal interface IHardExecutionBinding
{
    /// <summary>Binds this composition to an execution, or refuses if it is already another's.</summary>
    void BindTo(Guid executionId);

    /// <summary>
    /// Binds, establishes the live boundary, and measures it. Null when the execution may proceed.
    ///
    /// The one call a port makes before doing anything an execution's isolation is supposed to
    /// contain. It exists so that no port has to remember the order - bind, acquire, measure - and
    /// so that a port which forgot would not be the thing that ran work outside a boundary.
    /// </summary>
    Task<string?> EnsureBoundaryAsync(Guid executionId, CancellationToken ct);
}

/// <summary>
/// Whether the boundary an execution just built is sound. Held apart so the environment can be
/// measured without the environment knowing how.
/// </summary>
internal interface IHardBoundaryGate
{
    Task<string?> MeasureAsync(
        HardExecutionLease lease,
        HardIsolationProfile profile,
        string organizationSlug,
        CancellationToken ct);
}

/// <summary>
/// The production gate: the trusted plane's own verification, plus a reach probe from the placement
/// the workload will get.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MeasuredHardBoundaryGate : IHardBoundaryGate
{
    private readonly IHardNetworkFabric _fabric;
    private readonly WslHardNetworkProbe _probe;
    private readonly Func<bool> _peerApplicable;

    public MeasuredHardBoundaryGate(
        IHardNetworkFabric fabric, WslHardNetworkProbe probe, Func<bool> peerApplicable)
    {
        _fabric = fabric;
        _probe = probe;
        _peerApplicable = peerApplicable;
    }

    public Task<string?> MeasureAsync(
        HardExecutionLease lease,
        HardIsolationProfile profile,
        string organizationSlug,
        CancellationToken ct) =>
        PostAcquireHardBoundary.MeasureAsync(
            _fabric, _probe, lease, profile, organizationSlug, _peerApplicable(), ct);
}

/// <summary>
/// A binding for a hard surface that is being exercised on its own.
///
/// Not a production shape and deliberately not reachable from one. A focused test drives one
/// isolated surface without a composition around it, and this records what that surface claimed to
/// be acting for so the claim can still be asserted on. It refuses an empty identity for the same
/// reason the real resolver does - a surface acting for no execution is the defect, not the test
/// setup - and it never hands out an environment, because there is none.
/// </summary>
internal sealed class RecordedHardExecutionBinding : IHardExecutionBinding
{
    private readonly List<Guid> _bound = [];

    public IReadOnlyList<Guid> Bound => _bound;

    public void BindTo(Guid executionId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);

        _bound.Add(executionId);
    }

    /// <summary>What the boundary would have said. A focused test decides; nothing is acquired.</summary>
    public string? Refusal { get; set; }

    public Task<string?> EnsureBoundaryAsync(Guid executionId, CancellationToken ct)
    {
        BindTo(executionId);

        return Task.FromResult(Refusal);
    }
}

/// <summary>
/// The one thing that decides which execution a composition's hard ports belong to.
///
/// It exists because of an ordering problem that has no other honest answer. A composition is
/// assembled before an execution row exists - the workspace, the agent, the verifier and the
/// reviewer all have to be constructed first - but every isolated resource an execution takes must
/// be recorded against that execution's durable identity, or a crash leaves a live namespace holder
/// that nothing can be reconciled against. Binding at construction is impossible; binding per port
/// would let five ports bind five different ways.
///
/// So the resolver is created empty and adopts an identity exactly once, at first use, which is
/// after admission has decided the execution may run at all. Every hard port shares this one
/// instance, so "the agent, the verification commands and the reviewer ran in the same namespace" is
/// a fact about the object graph rather than a convention five adapters happen to follow.
///
/// Creating one takes nothing. No claim, no holder, no veth, no unit, no distribution started - a
/// resolver that provisioned anything would be provisioning it for executions that are about to be
/// refused.
///
/// Rebinding is refused rather than accommodated. A second identity arriving here means two
/// executions are sharing one composition, and the safe reading of that is not "serve them both" -
/// it is that one of them would silently run inside the other's boundary and both would look fine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionEnvironmentResolver : IHardExecutionBinding, IAsyncDisposable
{
    private readonly HardNetworkConsumers _consumers;
    private readonly IHardEnvironmentChannel _channel;
    private readonly IHardBoundaryGate _boundary;
    private readonly HardIsolationProfile _profile;
    private readonly string _organizationSlug;
    private readonly Lock _gate = new();

    private HardExecutionEnvironment? _environment;
    private Guid _boundTo;
    private bool _disposed;

    internal HardExecutionEnvironmentResolver(
        HardNetworkConsumers consumers,
        IHardEnvironmentChannel channel,
        IHardBoundaryGate boundary,
        TrustedGuestFileTransfer guest,
        HardIsolationProfile profile,
        string organizationSlug)
    {
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        Guest = guest ?? throw new ArgumentNullException(nameof(guest));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _organizationSlug = organizationSlug ?? throw new ArgumentNullException(nameof(organizationSlug));
    }

    /// <summary>
    /// The trusted control channel into this organization's environment.
    ///
    /// Separate from the execution's boundary on purpose, and it is the distinction between the two
    /// kinds of work the Hub does inside a distribution. Resolving what a verification tool is and
    /// reading a report back are the Hub's own bounded commands as the trusted service identity;
    /// they need no network namespace and taking one to perform them would mean acquiring a live
    /// boundary in order to read a dictionary. Running anything the repository influenced is the
    /// other kind, and that only ever happens through a lease.
    /// </summary>
    internal TrustedGuestFileTransfer Guest { get; }

    /// <summary>This organization's declared environment.</summary>
    public HardIsolationProfile Profile => _profile;

    /// <summary>The execution this composition is bound to, or empty while it is still unbound.</summary>
    public Guid BoundExecutionId
    {
        get
        {
            lock (_gate)
            {
                return _boundTo;
            }
        }
    }

    /// <summary>Whether anything has ever taken a live environment through this resolver.</summary>
    public bool HasLiveEnvironment
    {
        get
        {
            lock (_gate)
            {
                return _environment is { WasAcquired: true };
            }
        }
    }

    /// <summary>
    /// Builds a resolver for one organization, or explains why this machine has none.
    ///
    /// The channel is opened here, which means only that the Hub has established it could reach a
    /// distribution if asked. Nothing is started, entered or claimed.
    /// </summary>
    internal static bool TryCreate(
        HardNetworkConsumers consumers,
        IHardNetworkFabric fabric,
        Func<bool> peerApplicable,
        HardIsolationProfile profile,
        string organizationSlug,
        string hostWorkingDirectory,
        out HardExecutionEnvironmentResolver? resolver,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(profile);

        resolver = null;

        var channel = new WslHardEnvironmentChannel(hostWorkingDirectory);

        if (!channel.TryOpen(out var gateway, out problem) || gateway is null)
        {
            return false;
        }

        resolver = new HardExecutionEnvironmentResolver(
            consumers,
            channel,
            new MeasuredHardBoundaryGate(
                fabric, WslHardNetworkProbe.Create(hostWorkingDirectory), peerApplicable),
            new TrustedGuestFileTransfer(gateway, profile),
            profile,
            organizationSlug);

        return true;
    }

    /// <summary>
    /// The environment for this execution, adopting the identity if this is the first time.
    ///
    /// Thread safe, exactly once and fail closed. Two ports racing on the first call get the same
    /// environment; a port arriving later with the same identity gets the same environment; a port
    /// arriving with a different one gets an exception rather than a second environment.
    /// </summary>
    public void BindTo(Guid executionId) => Bind(executionId);

    public async Task<string?> EnsureBoundaryAsync(Guid executionId, CancellationToken ct)
    {
        var environment = Bind(executionId);

        return await environment.LeaseAsync(ct) is null
            ? environment.Refusal?.ToString() ?? "the hard isolation boundary could not be established."
            : null;
    }

    public HardExecutionEnvironment Bind(Guid executionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Empty is not a default, an unknown or a way to say "logical". It is a caller that does not
        // know which execution it is acting for, and every decision downstream needs that answer.
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);

        lock (_gate)
        {
            if (_environment is { } bound)
            {
                if (_boundTo != executionId)
                {
                    throw new InvalidOperationException(
                        $"this hard execution environment is bound to execution {_boundTo:D} and was asked "
                        + $"for {executionId:D}. One composition runs one execution: serving both would put "
                        + "one execution's work inside the other's boundary, and every log line would still "
                        + "say each was isolated.");
                }

                return bound;
            }

            _boundTo = executionId;

            _environment = new HardExecutionEnvironment(
                _consumers, _channel, _boundary, _profile, _organizationSlug, executionId);

            return _environment;
        }
    }

    /// <summary>
    /// Releases the environment if one was ever taken.
    ///
    /// Owned here and never by an adapter. A verifier that released what it finished with would tear
    /// the boundary down before review ran, and a resolver that never bound anything disposes to
    /// nothing because nothing was taken.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        HardExecutionEnvironment? environment;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            environment = _environment;
        }

        if (environment is not null)
        {
            await environment.DisposeAsync();
        }
    }
}
