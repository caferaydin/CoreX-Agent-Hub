using System.Runtime.Versioning;
using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Composition.HardNetwork;

/// <summary>What one reconciliation pass found, for an operator to read.</summary>
public sealed record HardNetworkReconciliation(
    int ClaimsDropped,
    int AttachmentsReleased,
    IReadOnlyList<string> Preserved,
    IReadOnlyList<string> Residual)
{
    public bool MadeNoChanges => ClaimsDropped == 0 && AttachmentsReleased == 0;

    public string Describe()
    {
        var parts = new List<string>();

        if (ClaimsDropped > 0)
        {
            parts.Add($"{ClaimsDropped} stale network claim(s) dropped");
        }

        if (AttachmentsReleased > 0)
        {
            parts.Add($"{AttachmentsReleased} unused organization attachment(s) released");
        }

        if (Preserved.Count > 0)
        {
            parts.Add($"{Preserved.Count} unrecognised item(s) preserved");
        }

        parts.AddRange(Residual);

        return parts.Count == 0 ? "nothing to reconcile" : string.Join("; ", parts);
    }
}

/// <summary>
/// Brings the durable network claims back into agreement with what the executions actually are.
///
/// A Hub that dies mid execution leaves two things behind: a row that still says Running, and a
/// claim on an organization's network attachment. The first is already reconciled - the stale
/// execution reconciler marks it Interrupted - and this runs immediately afterwards so that it
/// judges claims against settled truth rather than against a state somebody is still deciding.
/// The order matters in one direction only: dropping a claim first and then discovering its
/// execution was legitimately live would be releasing a namespace somebody is working in.
///
/// It is deliberately not a second lifecycle authority. It asks the store what an execution is and
/// believes the answer; it has no opinion about what Interrupted means and cannot produce one.
///
/// Everything ambiguous is preserved. An attachment directory that maps to no organization this
/// Hub knows about, a marker whose name is not an execution identity, a claim whose execution has
/// vanished entirely - none of those are deleted, because the cost of being wrong is asymmetric.
/// Leaving an unused attachment alive is visible and releasable later; tearing down one that is in
/// use is not recoverable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardNetworkConsumerReconciler
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _hardOrganizations;
    private readonly Func<CancellationToken, Task<IReadOnlySet<Guid>>> _entitledExecutions;
    private readonly HardNetworkConsumers _consumers;
    private readonly IHardIsolationProfileSource _profiles;
    private readonly IHardNetworkFabric _fabric;

    /// <summary>
    /// Built from the two facts it needs rather than from the whole store.
    ///
    /// Which organizations are hard, and which executions may still be inside one. Both are the
    /// lifecycle's answers and neither is this type's to decide - taking them as questions keeps
    /// that true, and keeps a reconciler that must never invent an execution state from having the
    /// vocabulary to.
    /// </summary>
    public HardNetworkConsumerReconciler(
        Func<CancellationToken, Task<IReadOnlyList<string>>> hardOrganizations,
        Func<CancellationToken, Task<IReadOnlySet<Guid>>> entitledExecutions,
        HardNetworkConsumers consumers,
        IHardIsolationProfileSource profiles,
        IHardNetworkFabric fabric)
    {
        _hardOrganizations = hardOrganizations ?? throw new ArgumentNullException(nameof(hardOrganizations));
        _entitledExecutions = entitledExecutions ?? throw new ArgumentNullException(nameof(entitledExecutions));
        _consumers = consumers ?? throw new ArgumentNullException(nameof(consumers));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
    }

    /// <summary>The production shape, reading both facts from the catalogue.</summary>
    public static HardNetworkConsumerReconciler Over(
        IHubStore store,
        HardNetworkConsumers consumers,
        IHardIsolationProfileSource profiles,
        IHardNetworkFabric fabric)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new HardNetworkConsumerReconciler(
            async ct => (await store.ListOrganizationsAsync(ct))
                .Where(o => o.IsolationLevel == IsolationLevel.Hard)
                .Select(o => o.Slug.Value)
                .ToList(),

            // Only a non terminal execution may still be inside a namespace. Everything else has
            // finished, whatever it finished as - including the Interrupted rows the stale
            // execution reconciler writes immediately before this runs.
            async ct => (await store.ListNonTerminalExecutionsAsync(ct)).Select(e => e.Id).ToHashSet(),
            consumers,
            profiles,
            fabric);
    }

    /// <summary>
    /// Drops claims nobody is entitled to any more, and releases what that leaves unused.
    ///
    /// Driven from the organizations this Hub actually knows about rather than from the directory
    /// listing. A name on disk is not authority to tear down a network attachment: the identity is
    /// derived from a trusted organization and matched against the registry, so an entry that maps
    /// to nothing recognised is reported and left exactly where it is.
    /// </summary>
    public async Task<HardNetworkReconciliation> ReconcileAsync(CancellationToken ct)
    {
        // Driven from what is actually on disk. Nothing here runs unless a claim exists, which
        // keeps an ordinary command that opens a session from reaching into an environment at all:
        // no registry, no work, no WSL call.
        var claimed = _consumers.ClaimedAttachments();

        if (claimed.Count == 0)
        {
            return new HardNetworkReconciliation(0, 0, [], []);
        }

        var organizations = await _hardOrganizations(ct);
        var entitled = await _entitledExecutions(ct);

        var known = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        var released = 0;
        var residual = new List<string>();

        foreach (var slug in organizations)
        {
            if (!HardNetworkIdentity.IsNameable(slug))
            {
                continue;
            }

            var attachmentId = HardNetworkIdentity.AttachmentId(slug);

            known.Add(attachmentId);

            if (!claimed.Contains(attachmentId, StringComparer.Ordinal))
            {
                // No claims were ever recorded for this organization, so there is nothing this pass
                // owns. Reaching for the environment to check would be this reconciler deciding to
                // touch a machine it has no reason to.
                continue;
            }

            // The verdict comes from here; the registry only applies it. An ownership store that
            // decided what an execution was would be a second opinion about the lifecycle.
            dropped += _consumers.Forget(slug, entitled.Contains).Count;

            if (_consumers.Consumers(slug).Count > 0)
            {
                // Somebody is still entitled to this organization's attachment.
                continue;
            }

            if (!_profiles.TryGetProfile(slug, out var profile, out _) || profile is null)
            {
                // Declared hard with no usable configuration: there is nothing this may safely act
                // on, and guessing at an environment is exactly what must not happen.
                continue;
            }

            // Nobody is left. Whatever this organization still has is the Hub's own to release,
            // through the frozen physical primitive and nothing else.
            try
            {
                await _fabric.ReleaseAsync(profile, slug, ct);
                released++;
            }
            catch (Exception ex)
            {
                // Reported, not retried into a loop. A Hub owned attachment with no consumers stays
                // findable, and the next pass may succeed where this one did not.
                residual.Add($"the attachment for '{slug}' could not be released: {ex.Message}");
            }
        }

        // Anything the registry holds that does not belong to an organization this Hub knows about.
        var preserved = _consumers.ClaimedAttachments()
            .Where(id => !known.Contains(id))
            .ToList();

        return new HardNetworkReconciliation(dropped, released, preserved, residual);
    }

}
