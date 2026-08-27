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
/// Whether this build can actually run a whole task inside a hard isolated environment.
///
/// Deliberately a first class question rather than an assumption, and deliberately about the
/// <b>whole task</b>. That distinction became load bearing once a hard process primitive
/// existed: <see cref="IHardProcessExecutor"/> can now run a contained process inside a WSL2
/// environment - correct identity, working directory, environment, process and memory ceilings,
/// runtime ceiling and whole-tree cancellation - and that is not the same capability as running
/// a Hub task there.
///
/// A Hub task also prepares a workspace, builds a context pack, drives git, verifies, reviews
/// and writes evidence, and every one of those still happens on the host. Admitting an
/// organization because a process can be contained would mean the agent ran inside the boundary
/// while the repository, the credentials and the Hub's own record of the run stayed outside it -
/// which is not isolation, and would be much harder to notice than an honest refusal.
///
/// So this answers no, and will continue to until the whole workflow moves. The alternative -
/// quietly running the work on Windows instead - is precisely the failure hard isolation exists
/// to prevent. There is no fallback path, and this port is where that is stated out loud.
/// </summary>
public interface IHardExecutionPlane
{
    bool IsAvailable { get; }

    /// <summary>Why not, in a sentence an operator can act on. Empty when available.</summary>
    string UnavailableReason { get; }
}

/// <summary>
/// The execution plane as this build ships it: absent, and saying so.
///
/// Not a stub standing in for something that works, and - since Phase 10C-2 - not a placeholder
/// waiting only for a process runner either. The process runner exists. What is absent is
/// everything else a task needs in order to be inside the boundary rather than half inside it:
/// the repository, the context, git, verification, review, evidence, provider authentication and
/// control plane isolation.
/// </summary>
public sealed class UnavailableHardExecutionPlane : IHardExecutionPlane
{
    public bool IsAvailable => false;

    public string UnavailableReason =>
        "this build can verify a hard isolated environment and run a contained process inside one, "
        + "but a whole task still prepares its workspace, context, git, verification, review and "
        + "evidence on the host; running it that way would leave the work half outside the boundary, "
        + "so it is refused.";
}
