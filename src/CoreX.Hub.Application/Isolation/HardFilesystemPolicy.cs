namespace CoreX.Hub.Application.Isolation;

/// <summary>
/// A filesystem surface a hard isolated workload may not reach, and why.
///
/// The reason travels with the path for the same purpose it does on a denied network destination:
/// a path without one is a path nobody can later judge, and the safe-looking edit is to delete it.
/// </summary>
/// <param name="Path">The subtree to close.</param>
/// <param name="Reason">Why it is closed. Not decoration; see above.</param>
/// <param name="Optional">
/// Whether the environment is allowed not to have it.
///
/// A path that must exist and does not is a mis-specified boundary, and the launch is refused -
/// measured, and it is how a typo in this list becomes a failed run rather than a silent hole. But
/// a surface that only exists while something else is running is a different case: demanding it
/// would refuse every workload on a machine where that thing is switched off, which is a boundary
/// that fails in the direction of not running at all. There is nothing to hide when it is absent,
/// and a namespace established without it does not acquire it later.
/// </param>
public sealed record DeniedSurface(string Path, string Reason, bool Optional = false);

/// <summary>
/// What a hard isolated workload may and may not reach on the filesystem.
///
/// The surface this closes was measured rather than assumed. Turning off automount, interop and
/// the imported Windows path leaves a WSL2 environment with no host volume and no Windows binaries
/// - and still leaves <c>/mnt/wsl</c>, a tmpfs mounted from the same kernel device in every
/// distribution on the machine. Two disposable environments were used to establish that plainly:
/// one wrote a file there and the other read it back, and then the other way round. It is not a
/// window onto the host, which is exactly what makes it easy to miss; it is a channel between
/// organizations, and the agent account can write to it.
///
/// So an explicit network namespace does not finish the job. Two organizations can be perfectly
/// separated on the network and still pass a file to each other through a shared mount, which is
/// why this boundary exists beside that one rather than instead of it.
///
/// <c>/mnt/wslg</c> was measured to be per environment on this build - the two disposable
/// environments had different kernel devices there, and a file written in one did not appear in
/// the other. It is denied anyway. It carries a read only bind of the environment's whole root
/// filesystem and live sockets belonging to the graphical stack, none of which a headless coding
/// agent needs, and "shared today" is not the kind of thing worth re-measuring on every WSL
/// update.
///
/// The claim is about a workload, not about a distribution. The environment still has these mounts
/// and the trusted service identity still sees them, because the Hub has to be able to prepare and
/// verify what it is hiding. What is established here is that the untrusted workload's own mount
/// namespace cannot reach them - see <see cref="HardIsolationChecks.SharedFilesystemUnreachable"/>,
/// which is answered by running as the agent rather than by reading a mount table.
///
/// Closed rather than configurable, exactly as the network policy is. There is one instance and no
/// builder, so an organization cannot widen its own boundary and a task cannot ask for a weaker
/// one.
/// </summary>
public sealed record HardFilesystemPolicy
{
    private HardFilesystemPolicy()
    {
    }

    /// <summary>The one policy this build enforces.</summary>
    public static HardFilesystemPolicy Phase10 { get; } = new();

    /// <summary>
    /// The surfaces an untrusted workload must not be able to reach.
    ///
    /// Denied as whole subtrees rather than as the individual files under them. A deny list of
    /// leaves would be a list somebody has to keep current against a filesystem two other products
    /// populate.
    /// </summary>
    public IReadOnlyList<DeniedSurface> DeniedSurfaces { get; } =
    [
        new("/mnt/wsl",
            "One tmpfs, the same kernel device in every distribution on the machine, writable by the "
            + "agent account; measured carrying a file from one organization's environment to another's."),

        new("/mnt/wslg",
            "The graphical stack's tmpfs. Per environment on this build, but it carries a read only "
            + "bind of the whole root filesystem and live sockets, and no coding agent needs either."),

        new("/run/user",
            "Bound from the graphical stack's tmpfs rather than being ordinary runtime state, so it is "
            + "the same surface reached by a second name."),

        new("/tmp/.X11-unix",
            "Also bound from the graphical stack's tmpfs, so the same surface is reachable through "
            + "/tmp however /mnt is closed.",
            Optional: true),
    ];

    /// <summary>
    /// Whether the workload may see processes that are not its own.
    ///
    /// A hidden <c>/proc</c> is part of the mount boundary rather than a separate hardening
    /// measure: <c>/proc/&lt;pid&gt;/root</c> of a process outside the sandbox is that process's
    /// filesystem view, so a visible neighbour running as the same account would be a second route
    /// to everything denied above.
    /// </summary>
    public bool HideOtherProcesses => true;

    /// <summary>
    /// Whether the workload runs with an empty capability bounding set.
    ///
    /// The workload has no effective capabilities either way. What the bounding set closes is the
    /// regaining of them - through a setuid binary the environment happens to ship - by something
    /// that has already gone wrong inside the sandbox.
    /// </summary>
    public bool DropAllCapabilities => true;

    /// <summary>
    /// The paths a workload must still be able to use, named so a test can assert both directions.
    ///
    /// A sandbox that denied everything would pass every negative test in this phase and be
    /// useless. Readiness treats a failure here as not ready rather than as a pass, because an
    /// environment where the workspace stopped working is not an environment anything should run
    /// in.
    /// </summary>
    public IReadOnlyList<string> RequiredWritableRoots(HardIsolationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return [profile.ExecutionRoot];
    }
}
