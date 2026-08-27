namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// One organization's place on the trusted network fabric.
///
/// Everything here is derived by the Hub from the organization's own identity and nothing else. A
/// task cannot ask for an attachment, a provider cannot name one, and a repository cannot
/// influence which one it gets - because the attachment <i>is</i> the boundary, and a boundary a
/// caller can choose is a boundary a caller can leave.
///
/// The reference is what a workload is launched against. It names a live holder rather than a
/// stable path on purpose: the platform's named namespace handles were measured not to survive the
/// session that created them, and a launch against a dead reference has to fail rather than land
/// somewhere plausible.
/// </summary>
public sealed record HardNetworkAttachment
{
    /// <summary>The Hub's stable name for this attachment. Derived, never supplied.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>
    /// The trusted interface the fabric classifies this organization's traffic by.
    ///
    /// Identity is the interface, never the source address. An address is configuration inside a
    /// namespace; an ingress interface is a fact about where the packet entered the trusted plane,
    /// and a workload cannot forge the latter by changing the former.
    /// </summary>
    public required string TrustedInterface { get; init; }

    /// <summary>The interface inside the organization's own namespace.</summary>
    public required string NamespaceInterface { get; init; }

    /// <summary>The attachment's bounded subnet, as a CIDR.</summary>
    public required string Subnet { get; init; }

    /// <summary>The trusted side's address on that subnet.</summary>
    public required string GatewayAddress { get; init; }

    /// <summary>The organization side's address on that subnet.</summary>
    public required string NamespaceAddress { get; init; }

    /// <summary>What a workload is launched against, resolved at launch and never cached.</summary>
    public required string NamespaceReference { get; init; }

    /// <summary>
    /// The kernel identity of the namespace, as measured.
    ///
    /// Kept because "the unit started" is not evidence that it started in the right place - a
    /// launch mechanism that silently substitutes a fresh namespace was measured on this platform,
    /// and this is what a verification compares against.
    /// </summary>
    public required string NamespaceIdentity { get; init; }
}

/// <summary>
/// The trusted network plane, and one organization's attachment to it.
///
/// Two truths, deliberately separated. The fabric is machine-global: one shared plane, one policy,
/// one set of trusted interfaces, and it is the Hub's own infrastructure rather than any
/// organization's. The attachment is an organization's, and no organization's attachment may ever
/// be satisfied by another's - which is a real risk here rather than a theoretical one, because on
/// this platform every environment shares one network namespace and one ruleset, so a check that
/// asked only "is the policy loaded" would answer yes for an organization that has no attachment
/// at all.
///
/// Preparation is trusted mutation of machine-global state and is not something a diagnostic does.
/// Verification changes nothing and may be called by anybody.
/// </summary>
public interface IHardNetworkFabric
{
    /// <summary>
    /// Brings the trusted plane and this organization's attachment into the expected state.
    ///
    /// Idempotent, and the only operation here that changes anything. Called from the trusted
    /// preparation path before a workload exists - never from a diagnostic, because a diagnostic
    /// that provisioned what it measured could always report success.
    /// </summary>
    Task<HardNetworkAttachment?> PrepareAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct);

    /// <summary>
    /// Says what is actually there, and changes nothing.
    ///
    /// Answers both truths at once because they can disagree in the direction that matters: the
    /// policy was measured to survive a restart that destroys the namespaces and interfaces it
    /// names, so a loaded table proves nothing on its own.
    /// </summary>
    Task<IReadOnlyList<ReadinessCheck>> VerifyAsync(
        HardIsolationProfile profile, string organizationSlug, CancellationToken ct);

    /// <summary>
    /// Gives up this organization's attachment, and the plane too if it was the last one.
    ///
    /// Global state the Hub turned on is turned back off only when nothing else needs it, and
    /// state the Hub never established is left exactly as it was found.
    /// </summary>
    Task ReleaseAsync(HardIsolationProfile profile, string organizationSlug, CancellationToken ct);
}
