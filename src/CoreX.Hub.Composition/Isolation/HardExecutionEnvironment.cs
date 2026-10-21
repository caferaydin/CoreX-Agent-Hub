using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.HardNetwork;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// One execution's hard environment, acquired once and shared by everything that needs it.
///
/// The lease cannot be taken when the composition is built, because at that point nobody has
/// decided whether this execution is allowed to run at all - admission happens inside the
/// orchestrator, and preparing a network namespace for an execution that is about to be refused
/// would be the diagnostic provisioning what it measures. So the lease is taken on first use,
/// which is the workspace preparation step, and that step only happens after admission passed.
///
/// Taken once, and that is the whole point of this type existing rather than each adapter
/// acquiring what it needs. The agent, the verification commands and the reviewer must land in the
/// same network namespace; if each of them prepared its own, they would each be isolated and the
/// execution would not be, and every test that checked one of them in isolation would still pass.
///
/// Disposal belongs to whoever owns the execution, never to an adapter. A verifier that released
/// the environment when it finished would tear the boundary down before review ran.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardExecutionEnvironment : IAsyncDisposable
{
    private readonly HardNetworkConsumers _consumers;
    private readonly IHardEnvironmentChannel _channel;
    private readonly IHardBoundaryGate _boundary;
    private readonly HardIsolationProfile _profile;
    private readonly string _organizationSlug;
    private readonly Guid _executionId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HardExecutionLease? _lease;
    private HardLeaseRefusal? _refusal;
    private bool _disposed;

    internal HardExecutionEnvironment(
        HardNetworkConsumers consumers,
        IHardEnvironmentChannel channel,
        IHardBoundaryGate boundary,
        HardIsolationProfile profile,
        string organizationSlug,
        Guid executionId)
    {
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _organizationSlug = organizationSlug ?? throw new ArgumentNullException(nameof(organizationSlug));

        // An environment is always somebody's. There is no anonymous one to adopt an identity
        // later, because a live claim held under no execution is the state crash reconciliation
        // cannot classify.
        ArgumentOutOfRangeException.ThrowIfEqual(executionId, Guid.Empty);

        _executionId = executionId;
    }

    /// <summary>Why the environment could not be taken, once something has tried.</summary>
    public HardLeaseRefusal? Refusal => _refusal;

    /// <summary>The execution this environment belongs to, for recording what it left behind.</summary>
    public Guid ExecutionIdentity => _executionId;

    /// <summary>Whether an environment was ever actually taken.</summary>
    public bool WasAcquired => _lease is not null;

    /// <summary>This organization's environment, for the adapters that need to name it.</summary>
    public HardIsolationProfile Profile => _profile;

    /// <summary>
    /// The lease, taking it if this is the first time anybody asked.
    ///
    /// A refusal is remembered rather than retried. An execution whose namespace could not be
    /// established does not get a second chance halfway through: the first refusal is the answer
    /// for the whole run, so a later step cannot succeed where an earlier one failed and leave the
    /// execution half isolated.
    /// </summary>
    public async Task<HardExecutionLease?> LeaseAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_lease is not null || _refusal is not null)
        {
            return _lease;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_lease is not null || _refusal is not null)
            {
                return _lease;
            }

            var (lease, refusal) = await HardExecutionLease.AcquireAsync(
                _consumers, _channel, _profile, _organizationSlug, _executionId, ct);

            if (lease is null)
            {
                _refusal = refusal;
                return null;
            }

            // Gate B. The lease built a boundary; this establishes that the boundary is really there
            // and really contains. Admission could not ask this - the state did not exist yet - and
            // an execution that skipped it would be running repository-controlled work inside
            // something nobody had measured.
            //
            // Before the lease is handed to anything, so every hard port inherits one invariant: a
            // usable environment is a measured one.
            if (await _boundary.MeasureAsync(lease, _profile, _organizationSlug, ct) is { } unsound)
            {
                // Released rather than kept. A boundary that did not hold is not something to leave
                // running while an execution is refused.
                await lease.DisposeAsync();

                _refusal = new HardLeaseRefusal("boundary", unsound);

                return null;
            }

            _lease = lease;

            return _lease;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the environment, once.
    ///
    /// Called by the owner of the execution when the lifecycle is over - after review, not after
    /// the agent. Everything that outlives an execution by design, the workspace and the evidence
    /// and the result commit, is governed by its own frozen retention rules and is not touched.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_lease is not null)
        {
            await _lease.DisposeAsync();
        }

        _gate.Dispose();
    }
}
