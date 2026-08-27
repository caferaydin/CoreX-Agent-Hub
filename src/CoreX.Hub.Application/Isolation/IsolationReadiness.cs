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
    public const string OtherHardEnvironmentUnreachable = "peer.filesystem.unreachable";
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
    /// Whether the Hub's own network policy is loaded, and is the one the Hub expects.
    ///
    /// Not "are there some rules". The active managed table is read back and compared against the
    /// policy this build renders, because a table that exists and says something else is worse
    /// than no table: it looks like enforcement in every diagnostic.
    /// </summary>
    public const string NetworkPolicyEnforced = "network.policy.enforced";

    /// <summary>
    /// Whether another organization's environment is reachable over the network.
    ///
    /// Deliberately not folded into <see cref="OtherHardEnvironmentUnreachable"/>, which is about
    /// shared mounts. Two environments can be perfectly separated on the filesystem and sit on one
    /// subnet, and a single check reporting both would let either answer stand for the other.
    /// </summary>
    public const string PeerNetworkUnreachable = "peer.network.unreachable";

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
        ControlPlaneUnreachable,
        NetworkModeSupported,
        HostLoopbackDisabled,
        ProxyNotInherited,
        NetworkPolicyEnforced,
        PeerNetworkUnreachable,
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

    public bool IsReady => Checks.All(c => c.Status == ReadinessStatus.Pass);

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
