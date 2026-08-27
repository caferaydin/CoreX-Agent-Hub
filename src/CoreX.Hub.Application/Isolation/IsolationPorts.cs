namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// Asks the machine whether one organization's hard environment really exists.
///
/// A port, because the answer is entirely operating system specific and nothing above this
/// line is allowed to know that WSL is the mechanism. An implementation observes; it never
/// creates, installs, repairs or configures anything, and it never returns the content of a
/// file - existence, ownership, permission bits and filesystem types are enough to decide
/// every check, and they cannot leak a credential.
/// </summary>
public interface IHardIsolationReadinessProbe
{
    /// <summary>
    /// Observes the environment described by <paramref name="profile"/>.
    ///
    /// Never throws for a machine that is missing, broken or unreachable: that is an answer,
    /// and the answer is Unknown, which is not a pass. Exceptions are reserved for a caller
    /// that asked something impossible.
    /// </summary>
    Task<HardIsolationReadiness> ProbeAsync(
        string organizationSlug,
        HardIsolationProfile profile,
        CancellationToken ct);
}

/// <summary>
/// Where an organization's hard isolation configuration comes from.
///
/// Separate from the probe because "the operator described an environment" and "the machine
/// has one" are the two facts this slice exists to keep apart. A missing or malformed
/// description is a refusal in its own right, and never something a probe has to guess at.
/// </summary>
public interface IHardIsolationProfileSource
{
    /// <summary>Reads one organization's profile, or explains why there is not a usable one.</summary>
    bool TryGetProfile(string organizationSlug, out HardIsolationProfile? profile, out string? problem);
}

/// <summary>
/// Whether this build can actually run a task inside a hard isolated environment.
///
/// Deliberately a first class question rather than an assumption. Phase 10C-1 establishes the
/// gates and nothing else: readiness can be measured and the topology can be judged, but there
/// is no execution plane that runs an agent inside the environment yet. An organization whose
/// environment is perfect must therefore still be refused, because the alternative - quietly
/// running the work on Windows instead - would be precisely the failure hard isolation exists
/// to prevent. There is no fallback path, and this port is where that is stated out loud.
/// </summary>
public interface IHardExecutionPlane
{
    bool IsAvailable { get; }

    /// <summary>Why not, in a sentence an operator can act on. Empty when available.</summary>
    string UnavailableReason { get; }
}

/// <summary>
/// The execution plane as Phase 10C-1 ships it: absent, and saying so.
///
/// Not a stub standing in for something that works. It is the honest description of a build
/// that can measure a hard environment and cannot yet execute in one.
/// </summary>
public sealed class UnavailableHardExecutionPlane : IHardExecutionPlane
{
    public bool IsAvailable => false;

    public string UnavailableReason =>
        "this build can verify a hard isolated environment but cannot yet run a task inside one; "
        + "running the task on the host instead would defeat the isolation, so it is refused.";
}
