using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// The systemd properties that close the shared WSL filesystem surfaces before a workload starts.
///
/// One pure function, separated for the same reason <see cref="SystemdRunCommand"/> is: this is
/// where the boundary is actually requested, and a missing property produces a run that looks
/// entirely normal and is not contained. A test can compare the rendered vector against a literal
/// without a machine, and the machine tests then establish that the vector does what it says.
///
/// The shape was measured against systemd 257 in a disposable Debian environment rather than taken
/// from documentation, and each property earns its place:
///
///   PrivateMounts=yes        the unit gets its own mount namespace, which is what makes every
///                            path below a per workload fact rather than a change to the machine
///   InaccessiblePaths=       the denied surfaces, overmounted before exec; measured to also close
///                            traversal, symlinks, realpath and /proc/&lt;pid&gt;/root
///   ProtectProc=invisible    other processes' /proc entries, and with them their filesystem views
///   CapabilityBoundingSet=   nothing to regain through a setuid binary
///   NoNewPrivileges=yes      and no way to acquire more during exec
///
/// What this deliberately does not do is unmount anything. The surfaces stay exactly as they are
/// for the environment and for the trusted service identity - which is what lets the Hub verify
/// what it is hiding - and disappear with the unit, so there is no machine state to reconcile
/// afterwards and nothing that outlives a run.
///
/// Composition owns every value here. None of it is reachable from a request, a task, a provider
/// or repository content, so a workload cannot ask for a weaker boundary than the one this build
/// enforces.
/// </summary>
public static class HardMountSandbox
{
    /// <summary>
    /// The properties for one hard workload, in a fixed order.
    ///
    /// Ordered so the same policy always renders the same vector and a test can compare it against
    /// a literal rather than against a set.
    /// </summary>
    public static IReadOnlyList<string> Properties(HardFilesystemPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var properties = new List<string> { "PrivateMounts=yes" };

        // Ordered by path so the vector is stable whatever order the policy lists them in.
        foreach (var surface in policy.DeniedSurfaces.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            // A leading "-" is how systemd spells "and it is allowed not to be there". Without it a
            // missing path refuses the launch, which is what the load bearing surfaces want and
            // what a surface belonging to a service the machine may not be running does not.
            properties.Add("InaccessiblePaths=" + (surface.Optional ? "-" : string.Empty) + surface.Path);
        }

        if (policy.HideOtherProcesses)
        {
            properties.Add("ProtectProc=invisible");
        }

        if (policy.DropAllCapabilities)
        {
            // An empty value is the way systemd spells "none of them"; it is not a missing setting.
            properties.Add("CapabilityBoundingSet=");
            properties.Add("NoNewPrivileges=yes");
        }

        return properties;
    }

    /// <summary>
    /// Whether a built argument vector really carries the whole boundary.
    ///
    /// The launch path uses this on what it is about to run, so a vector that lost a property
    /// between rendering and exec is refused rather than executed. "The unit started" is not
    /// evidence of anything: a run missing one of these is not a slightly less isolated run, it is
    /// an unisolated one in the dimension that was left out.
    /// </summary>
    public static bool IsFullyApplied(IReadOnlyList<string> arguments, HardFilesystemPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var separator = arguments.ToList().IndexOf("--");

        // Only what systemd is being told counts. Everything after the separator is the workload's
        // own argument vector, and a workload that spelled a property there would be text.
        var options = separator < 0 ? arguments : arguments.Take(separator).ToList();

        return Properties(policy).All(
            property => options.Contains("--property=" + property, StringComparer.Ordinal));
    }
}
