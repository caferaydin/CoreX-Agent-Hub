namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// What one readiness check concluded.
///
/// Three values rather than two, and the third is the important one. A check that could not
/// be performed - because the mechanism does not exist yet, or the machine did not answer -
/// has not passed, and calling it a failure would suggest something was measured and found
/// wanting. <see cref="Unknown"/> says the honest thing: nobody knows. It counts as not ready
/// exactly like a failure does.
/// </summary>
public enum ReadinessStatus
{
    /// <summary>Measured, and the machine is as required.</summary>
    Pass,

    /// <summary>Measured, and the machine is not as required.</summary>
    Fail,

    /// <summary>Not established. Never a pass.</summary>
    Unknown,
}

/// <summary>
/// One named question about the machine, and the answer.
///
/// The id is stable and safe to print, log and compare; the detail is a sentence for a person
/// reading a diagnostic. Neither ever carries a secret - see the probe implementations, which
/// return existence, permission bits and filesystem types rather than content.
/// </summary>
public sealed record ReadinessCheck(string Id, ReadinessStatus Status, string Detail)
{
    public static ReadinessCheck Passed(string id, string detail) => new(id, ReadinessStatus.Pass, detail);

    public static ReadinessCheck Failed(string id, string detail) => new(id, ReadinessStatus.Fail, detail);

    public static ReadinessCheck Unknown(string id, string detail) => new(id, ReadinessStatus.Unknown, detail);
}

/// <summary>
/// The canonical set of questions a hard isolated environment has to answer.
///
/// Declared as a closed list so that "ready" cannot be reached by answering fewer questions
/// than the design requires. A result missing any of these is completed with
/// <see cref="ReadinessStatus.Unknown"/> before it is judged, which means a probe that forgot
/// to report something makes the environment not ready rather than accidentally ready.
/// </summary>
public static class HardIsolationChecks
{
    public const string EnvironmentExists = "environment.exists";
    public const string EnvironmentVersion = "environment.version";

    public const string AgentUserExists = "agent.user.exists";
    public const string AgentUserNotRoot = "agent.user.not-root";
    public const string AgentUserNotPrivileged = "agent.user.not-privileged";

    public const string HostDrivesNotMounted = "host.automount.disabled";
    public const string HostInteropDisabled = "host.interop.disabled";
    public const string HostPathNotImported = "host.path.not-imported";

    public const string ExecutionRootNative = "execution-root.native";
    public const string EvidenceRootNative = "evidence-root.native";
    public const string ExecutionRootUsable = "execution-root.usable";
    public const string EvidenceRootNotAgentReadable = "evidence-root.not-agent-readable";

    public const string SystemdAvailable = "systemd.available";
    public const string CgroupV2Available = "cgroup.v2";
    public const string MemoryControllerAvailable = "cgroup.controller.memory";
    public const string PidsControllerAvailable = "cgroup.controller.pids";

    public const string ProcessLimitDeclared = "policy.process-limit";
    public const string MemoryLimitDeclared = "policy.memory-limit";

    public const string HostProviderAuthUnreachable = "host.provider-auth.unreachable";

    /// <summary>
    /// Whether another organization's environment is reachable through a shared mount.
    ///
    /// Answered from inside a workload rather than from the environment's mount table, and the
    /// difference is the whole of this check. Every WSL distribution on the machine has
    /// <c>/mnt/wsl</c>, so a mount table reading can only ever say "yes it is there", which was
    /// this check's answer for as long as it read one - permanently not ready, for a condition
    /// nothing could clear. The boundary is not the distribution's; it is the workload's own mount
    /// namespace, and the only way to establish that is to run as the agent and try.
    /// </summary>
    public const string OtherHardEnvironmentUnreachable = "peer.filesystem.unreachable";

    /// <summary>
    /// Whether the shared WSL surfaces are unreachable from a workload, each one measured.
    ///
    /// Kept apart from the peer question above because the two fail differently and a single
    /// answer would let either stand for the other: this one says the surfaces are closed, and
    /// that one says a peer organization's marker could not be read through them. A sandbox that
    /// hid the wrong directory would satisfy the first and not the second.
    /// </summary>
    public const string SharedFilesystemUnreachable = "shared-filesystem.surface-unreachable";

    /// <summary>
    /// Whether a workload can still do its work inside that boundary.
    ///
    /// Deliberately part of readiness rather than a test only property. A boundary is trivially
    /// satisfiable by denying everything, and an environment whose workspace stopped working is
    /// not ready however clean its denials look.
    /// </summary>
    public const string WorkloadFilesystemUsable = "workload-filesystem.usable";

    public const string ControlPlaneUnreachable = "control-plane.unreachable";

    /// <summary>
    /// The topology the network boundary was designed against, and measured rather than declared.
    ///
    /// Mirrored networking gives the environment the host's own loopback, which would make every
    /// destination rule below beside the point: the control plane would be reachable at an address
    /// no destination policy denies. So mirrored is not a weaker supported mode, it is unsupported.
    /// </summary>
    public const string NetworkModeSupported = "network.mode.supported";

    /// <summary>
    /// Whether the host's loopback is bridged into the environment.
    ///
    /// A separate question from the mode, because it has its own switch and can be turned on
    /// under NAT. Both a configuration reading and a probe from the agent's own identity, since
    /// either alone answers a different question than the one being asked.
    /// </summary>
    public const string HostLoopbackDisabled = "network.host-loopback.disabled";

    /// <summary>
    /// Whether the environment inherited the host's proxy configuration.
    ///
    /// A host proxy is a host channel, and one that can carry credentials. Inherited or unknown
    /// is not ready; there is no supported way for a hard environment to borrow the developer's.
    /// </summary>
    public const string ProxyNotInherited = "network.proxy.not-inherited";

    /// <summary>
    /// Whether the trusted network plane itself is exactly what the Hub expects.
    ///
    /// Machine-global rather than per organization, because on this platform the network plane is:
    /// every environment shares one namespace, one loopback and one ruleset, so the plane is Hub
    /// infrastructure and not anybody's boundary.
    ///
    /// "The policy is loaded" is deliberately not enough. The policy was measured to survive a
    /// restart that destroys the namespaces and interfaces it names, so a table can list exactly
    /// the expected rules while classifying nothing at all. This passes only when every trusted
    /// interface the policy names is present and every present one is named.
    /// </summary>
    public const string NetworkFabricReady = "global-network-fabric.ready";

    /// <summary>
    /// Whether this organization has a network namespace of its own, held by somebody it is not.
    ///
    /// A namespace the agent could create or destroy would be a boundary the agent controls. The
    /// holder is the Hub's, and the namespace exists for exactly as long as the holder does.
    /// </summary>
    public const string OrgNetworkNamespaceReady = "org-network-namespace.ready";

    /// <summary>
    /// Whether a workload would actually land in that namespace.
    ///
    /// Separate from the namespace existing, because the two were measured to come apart: a launch
    /// against a missing namespace was observed starting the workload in a fresh empty one instead
    /// of failing. So the kernel identity a workload really gets is compared against the one the
    /// organization was assigned, and a mismatch is not an execution.
    /// </summary>
    public const string OrgNetworkAttachmentReady = "org-network-attachment.ready";

    /// <summary>
    /// Whether another organization's environment is reachable over the network.
    ///
    /// Deliberately not folded into <see cref="OtherHardEnvironmentUnreachable"/>, which is about
    /// shared mounts. Two environments can be perfectly separated on the filesystem and sit on one
    /// subnet, and a single check reporting both would let either answer stand for the other.
    /// </summary>
    public const string PeerNetworkUnreachable = "peer.network.unreachable";

    /// <summary>
    /// The live boundary this execution owns, measured only once it exists.
    ///
    /// These describe ephemeral state that an execution's own lease creates: the trusted plane's
    /// table, the organization's namespace holder, and whether a workload really lands in it. They
    /// were required before anything existed, which made the first hard execution impossible - the
    /// fabric is built by <c>HardExecutionLease</c>, the lease is taken after admission, and
    /// admission demanded the fabric. A gate cannot require the thing it gates the creation of.
    ///
    /// The answer is not to require less. It is to ask at the right time: nothing repository
    /// controlled runs until every one of these has passed, measured from the placement the workload
    /// will actually get.
    /// </summary>
    public static IReadOnlyList<string> PostAcquireRequired { get; } =
    [
        NetworkFabricReady,
        OrgNetworkNamespaceReady,
        OrgNetworkAttachmentReady,
        HostLoopbackDisabled,
        ControlPlaneUnreachable,
    ];

    /// <summary>
    /// Checks whose applicability depends on something outside this organization.
    ///
    /// Peer separation is the only one. It cannot be measured with no peer to fail to reach, and
    /// "there is no neighbour" is not the same statement as "the neighbour was reachable". Treating
    /// the first as the second refused every single-organization installation for a property it had
    /// no way to violate.
    ///
    /// It is conditional, not optional: the moment a co-resident hard environment exists, this is
    /// required and is proved behaviourally.
    /// </summary>
    public static IReadOnlyList<string> ConditionalOnPeer { get; } = [PeerNetworkUnreachable];

    /// <summary>Every check that must pass. All of them; there are no optional ones.</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        EnvironmentExists,
        EnvironmentVersion,
        AgentUserExists,
        AgentUserNotRoot,
        AgentUserNotPrivileged,
        HostDrivesNotMounted,
        HostInteropDisabled,
        HostPathNotImported,
        ExecutionRootNative,
        EvidenceRootNative,
        ExecutionRootUsable,
        EvidenceRootNotAgentReadable,
        SystemdAvailable,
        CgroupV2Available,
        MemoryControllerAvailable,
        PidsControllerAvailable,
        ProcessLimitDeclared,
        MemoryLimitDeclared,
        HostProviderAuthUnreachable,
        OtherHardEnvironmentUnreachable,
        SharedFilesystemUnreachable,
        WorkloadFilesystemUsable,
        ControlPlaneUnreachable,
        NetworkModeSupported,
        HostLoopbackDisabled,
        ProxyNotInherited,
        NetworkFabricReady,
        OrgNetworkNamespaceReady,
        OrgNetworkAttachmentReady,
        PeerNetworkUnreachable,
    ];

    /// <summary>
    /// What must hold before the Hub may attempt to build this execution's boundary.
    ///
    /// Everything measurable without an execution-owned live fabric: the environment, the
    /// identities, the roots, the kernel facilities, the host separation and the declared ceilings.
    /// Passing says the Hub may safely try - not that a boundary exists.
    /// </summary>
    public static IReadOnlyList<string> PreAcquireRequired { get; } =
    [
        .. Required.Where(id =>
            !PostAcquireRequired.Contains(id, StringComparer.Ordinal)
            && !ConditionalOnPeer.Contains(id, StringComparer.Ordinal)),
    ];
}

/// <summary>
/// Whether one organization's hard isolated environment actually exists on this machine.
///
/// This is machine truth, and it is deliberately not the same thing as
/// <c>IsolationLevel = Hard</c>. That value is an operator saying what they intend; this is
/// the result of asking the operating system whether the intent is real. An environment can
/// be declared hard in configuration and be nothing of the sort - a distro that was never
/// created, a user with sudo, a mount that still exposes the host - and the whole point of
/// this type is that the declaration cannot answer for the machine.
///
/// Ready means every required check passed. Not "no check failed": a missing or unknown check
/// is not a pass, so an environment is ready only when someone actually established that it is.
/// </summary>
public sealed record HardIsolationReadiness
{
    private HardIsolationReadiness(string organizationSlug, IReadOnlyList<ReadinessCheck> checks)
    {
        OrganizationSlug = organizationSlug;
        Checks = checks;
    }

    public string OrganizationSlug { get; }

    /// <summary>Every required check, in the canonical order, always complete.</summary>
    public IReadOnlyList<ReadinessCheck> Checks { get; }

    /// <summary>
    /// Every question answered as required, including the live boundary.
    ///
    /// The complete picture, for a diagnostic that wants one. It is deliberately not what admission
    /// asks: an idle organization has no live fabric, and demanding one before an execution exists is
    /// the circular dependency that made hard execution unreachable.
    /// </summary>
    public bool IsReady => IsReadyFor(HardIsolationChecks.Required);

    /// <summary>
    /// Whether the Hub may attempt to build this execution's boundary.
    ///
    /// Says nothing whatever about the boundary being up. The name is the promise: eligible to try.
    /// </summary>
    public bool IsAdmissionEligible => IsReadyFor(HardIsolationChecks.PreAcquireRequired);

    /// <summary>
    /// Whether the live boundary this execution owns is established.
    ///
    /// Asked after the lease has built it and before anything repository controlled runs. The peer
    /// question joins the required set only when a peer exists to be measured against - with none,
    /// it is not applicable rather than unmet, and reporting it as unmet would refuse an
    /// installation for a property it cannot violate.
    /// </summary>
    public bool IsLiveBoundaryReady(bool peerApplicable) =>
        IsReadyFor(
            peerApplicable
                ? [.. HardIsolationChecks.PostAcquireRequired, .. HardIsolationChecks.ConditionalOnPeer]
                : HardIsolationChecks.PostAcquireRequired);

    /// <summary>
    /// Whether every named check passed.
    ///
    /// Unknown is never a pass here either. A check nobody could perform has not established
    /// anything, and a phase that treated "no probe" as "no problem" would be the one place this
    /// design cannot afford optimism.
    /// </summary>
    public bool IsReadyFor(IReadOnlyList<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return required.All(id =>
            Checks.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)
                            && c.Status == ReadinessStatus.Pass));
    }

    /// <summary>The unmet ids among a named set, for a message that names the phase it refused.</summary>
    public IReadOnlyList<string> UnmetAmong(IReadOnlyList<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return
        [
            .. Checks
                .Where(c => required.Contains(c.Id, StringComparer.Ordinal) && c.Status != ReadinessStatus.Pass)
                .Select(c => c.Id),
        ];
    }

    /// <summary>The ids worth showing an operator: everything that is not a pass.</summary>
    public IReadOnlyList<string> UnmetCheckIds =>
        [.. Checks.Where(c => c.Status != ReadinessStatus.Pass).Select(c => c.Id)];

    /// <summary>
    /// Builds a complete result from whatever a probe managed to answer.
    ///
    /// Anything the probe did not report becomes Unknown rather than being left out, so the
    /// set of questions is fixed by this type and not by how far a probe happened to get.
    /// </summary>
    public static HardIsolationReadiness From(string organizationSlug, IEnumerable<ReadinessCheck> observed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);
        ArgumentNullException.ThrowIfNull(observed);

        var byId = new Dictionary<string, ReadinessCheck>(StringComparer.Ordinal);

        foreach (var check in observed)
        {
            // First answer wins, so a later duplicate cannot upgrade an earlier failure.
            byId.TryAdd(check.Id, check);
        }

        var complete = HardIsolationChecks.Required
            .Select(id => byId.TryGetValue(id, out var check)
                ? check
                : ReadinessCheck.Unknown(id, "This check was not performed."))
            .ToList();

        return new HardIsolationReadiness(organizationSlug, complete);
    }

    /// <summary>An environment nobody could look at. Every question unanswered.</summary>
    public static HardIsolationReadiness Unavailable(string organizationSlug, string detail) =>
        From(
            organizationSlug,
            HardIsolationChecks.Required.Select(id => ReadinessCheck.Unknown(id, detail)));
}
