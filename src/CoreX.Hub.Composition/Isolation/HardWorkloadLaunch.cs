using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Where a hard workload runs: the live network namespace it must join, and nothing else.
///
/// Composition owns this and creates it per execution from the attachment it prepared. It is
/// deliberately not part of <see cref="HardProcessRequest"/>: a request describes what the
/// workload is, and the workload has no business naming the boundary it will be held inside.
/// Keeping the two apart is what makes "the agent cannot choose its own namespace" a fact about
/// the types rather than a rule somebody has to remember.
/// </summary>
/// <param name="NetworkNamespacePath">
/// The reference systemd is given, of the form <c>/proc/&lt;holder-pid&gt;/ns/net</c>.
///
/// A path rather than a name because that is the form measured to fail closed: a reference whose
/// holder has died is refused before the executable starts, where the alternative spelling was
/// observed silently substituting a fresh empty namespace - which looks like isolation and is a
/// workload running somewhere nobody accounted for.
/// </param>
public sealed record HardWorkloadPlacement(string NetworkNamespacePath)
{
    /// <summary>Whether this is a usable reference rather than a blank one.</summary>
    public bool IsUsable => NetworkNamespacePath.StartsWith("/proc/", StringComparison.Ordinal)
                            && NetworkNamespacePath.EndsWith("/ns/net", StringComparison.Ordinal);
}

/// <summary>
/// The one place that says what containment a hard workload runs under.
///
/// Every untrusted process in a hard execution comes through here - the agent, the verification
/// commands, and review whenever review touches repository state - so that they cannot drift
/// apart. Three subtly different launch sites is the failure this exists to prevent: an agent
/// with a network namespace and a mount sandbox, a verifier with only the sandbox, and a reviewer
/// with neither would each look reasonable in its own file, and the weakest of them would be the
/// one the boundary is actually worth.
///
/// Both halves are mandatory together for repository-controlled code. A network-only unit can
/// read the shared WSL surfaces; a mount-only unit sits in the machine's root network namespace
/// beside every other organization. Neither is a hard workload, so this refuses to render one.
/// </summary>
public static class HardWorkloadLaunch
{
    /// <summary>
    /// The trusted systemd properties for one untrusted workload.
    ///
    /// Ordered and total: the filesystem boundary from the frozen policy, then the network
    /// namespace it must join. Nothing here is conditional on the request, because a run missing
    /// either half is not a slightly less isolated run.
    /// </summary>
    public static IReadOnlyList<string> Properties(HardWorkloadPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        return
        [
            .. HardMountSandbox.Properties(HardFilesystemPolicy.Phase10),
            "NetworkNamespacePath=" + placement.NetworkNamespacePath,
        ];
    }

    /// <summary>
    /// Whether a built argument vector really carries both boundaries.
    ///
    /// Used on the vector about to be executed rather than on the one that was rendered, so a
    /// property lost anywhere in between refuses the run. "The unit started" is not evidence of
    /// containment, and a unit that started with half of it is the case worth catching.
    /// </summary>
    public static bool IsFullyApplied(IReadOnlyList<string> arguments, HardWorkloadPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(placement);

        var separator = arguments.ToList().IndexOf("--");

        // Only what systemd is being told counts. Everything after the separator belongs to the
        // workload, and a workload that spelled a property there would be spelling text.
        var options = separator < 0 ? arguments : arguments.Take(separator).ToList();

        return Properties(placement).All(
            property => options.Contains("--property=" + property, StringComparer.Ordinal));
    }
}
