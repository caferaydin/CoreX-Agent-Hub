using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Composition.Isolation;

/// <summary>
/// Asks a WSL2 environment whether it is really the boundary its configuration claims.
///
/// This is the only place in the system that knows WSL exists. Above it there are readiness
/// checks and a verdict; below it there is one <c>wsl.exe</c> invocation per question, run
/// through the ordinary process boundary.
///
/// Three rules shape everything here:
///
///   * it observes and never changes anything. No distribution is created, imported,
///     terminated or configured, no user is added, no file is written and no mount is altered.
///     A machine that is not ready is reported, not repaired.
///   * an unanswerable question is Unknown. A command that could not run, a distribution that
///     is stopped and will not start, a timeout - none of those is a pass, and none of them is
///     reported as a failure of the thing being asked about either.
///   * nothing it returns can be a secret. Every check is decided from existence, exit codes,
///     uids, group names, permission bits, filesystem types and mount points. No file content
///     is read except two: the mount table and the cgroup controller list, both of which are
///     parsed down to those fields before anything is kept.
///
/// The one deliberate asymmetry is which identity asks. Questions about what the agent can
/// reach are asked as the agent, because that is the only identity whose answer matters;
/// questions about how the environment is built are asked as the service user, so that a
/// correctly locked down evidence directory does not report itself as missing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslReadinessProbe : IHardIsolationReadinessProbe
{
    private const string Id = "/usr/bin/id";
    private const string Cat = "/usr/bin/cat";
    private const string Stat = "/usr/bin/stat";
    private const string Test = "/usr/bin/test";
    private const string Printenv = "/usr/bin/printenv";
    private const string Systemctl = "/usr/bin/systemctl";

    /// <summary>
    /// Asked one question and only one: <c>-n -l</c>, which lists and runs nothing.
    ///
    /// Here because group membership is not the whole story - sudoers can name a user directly -
    /// and the claim this check makes has to be precise: no configured administrative privilege.
    /// </summary>
    private const string Sudo = "/usr/bin/sudo";

    /// <summary>The path every batched <c>stat</c> asks about. See <see cref="WslStatResponse"/>.</summary>
    private const string ControlPath = WslStatResponse.ControlPath;

    /// <summary>
    /// A directory that does not exist, handed to wsl.exe as its entire PATH.
    ///
    /// Synthetic on purpose. The host PATH import check needs something to look for, and the
    /// thing it looks for must not be a real path on the operator's machine: nothing about the
    /// host should be observable from inside an environment that has not yet been established.
    /// </summary>
    private const string PathMarker = "corex-hard-isolation-path-probe";

    private const string MarkerPath = @"C:\" + PathMarker;

    /// <summary>
    /// Systemd's own vocabulary for its state.
    ///
    /// Compared against rather than merely read, because a failed launch also produces a line
    /// of text, and text that is not one of these is not an answer about systemd.
    /// </summary>
    private static readonly HashSet<string> SystemdStates = new(StringComparer.Ordinal)
    {
        "initializing", "starting", "running", "degraded", "maintenance", "stopping", "offline", "unknown",
    };

    /// <summary>
    /// Groups that make an account able to leave its own boundary.
    ///
    /// Named rather than inferred. A uid that is not zero says nothing on its own: an account
    /// in <c>sudo</c> becomes root on request, one in <c>docker</c> or <c>lxd</c> asks a
    /// privileged daemon to do it, and one in <c>disk</c> or <c>shadow</c> reads what root
    /// reads without ever becoming root.
    /// </summary>
    private static readonly HashSet<string> PrivilegedGroups = new(StringComparer.Ordinal)
    {
        "root", "sudo", "wheel", "admin", "adm", "docker", "lxd", "kvm", "disk", "shadow",
    };

    private readonly IWslCommandRunner? _runner;
    private readonly string? _unavailable;
    private readonly char? _controlPlaneDrive;
    private readonly char? _hostProfileDrive;

    /// <summary>
    /// Who answers the network questions, when anybody does.
    ///
    /// Kept behind the Application's port so this file never learns a filtering mechanism. Absent
    /// is not permissive: with no network probe every network check is unknown, and unknown is not
    /// ready.
    /// </summary>
    private readonly IHardNetworkProbe? _network;

    private WslReadinessProbe(
        IWslCommandRunner? runner,
        string? unavailable,
        char? controlPlaneDrive,
        char? hostProfileDrive,
        IHardNetworkProbe? network)
    {
        _runner = runner;
        _unavailable = unavailable;
        _controlPlaneDrive = controlPlaneDrive;
        _hostProfileDrive = hostProfileDrive;
        _network = network;
    }

    /// <summary>
    /// Builds a probe for this installation.
    ///
    /// <paramref name="dataDirectory"/> is both the working directory every probe process runs
    /// in and the location whose reachability the control plane check is about - the Hub's
    /// database and evidence live there, and an agent that can reach them can rewrite the
    /// record of its own run.
    /// </summary>
    public static WslReadinessProbe Create(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var controlPlaneDrive = DriveLetterOf(dataDirectory);
        var hostProfileDrive = DriveLetterOf(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var network = HardNetwork.WslHardNetworkProbe.Create(dataDirectory);

        return WslCommandRunner.TryCreate(dataDirectory, out var runner, out var problem)
            ? new WslReadinessProbe(runner, null, controlPlaneDrive, hostProfileDrive, network)
            : new WslReadinessProbe(null, problem, controlPlaneDrive, hostProfileDrive, network);
    }

    /// <summary>
    /// A probe over a scripted command runner, for this assembly's tests.
    ///
    /// The readiness decisions are the whole point of this type, and most of them cannot be
    /// exercised against a real machine without breaking it: an environment whose agent is in
    /// the sudo group, whose evidence directory the agent can read, or whose kernel has no pids
    /// controller is not something a test may arrange on the operator's laptop. Scripting the
    /// answers proves the judgement; the integration suite separately proves that the real
    /// runner asks a real environment the same questions.
    /// </summary>
    internal static WslReadinessProbe Scripted(
        IWslCommandRunner runner,
        char? controlPlaneDrive = null,
        char? hostProfileDrive = null,
        IHardNetworkProbe? network = null) =>
        new(runner, null, controlPlaneDrive, hostProfileDrive, network);

    public async Task<HardIsolationReadiness> ProbeAsync(
        string organizationSlug,
        HardIsolationProfile profile,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationSlug);
        ArgumentNullException.ThrowIfNull(profile);

        var checks = new List<ReadinessCheck>();

        // Answerable without the machine, and still a check: an environment with no declared
        // ceiling is one that cannot be given one when the execution plane arrives.
        checks.Add(profile.ProcessLimit > 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.ProcessLimitDeclared,
                $"A ceiling of {profile.ProcessLimit} concurrent processes is declared.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.ProcessLimitDeclared, "No process ceiling is declared."));

        checks.Add(profile.MemoryLimitBytes > 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.MemoryLimitDeclared,
                $"A ceiling of {profile.MemoryLimitBytes / (1024 * 1024)} MiB of memory is declared.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.MemoryLimitDeclared, "No memory ceiling is declared."));

        if (profile.Backend != HardIsolationBackend.Wsl2)
        {
            return Complete(organizationSlug, checks, $"'{profile.Backend}' is not probed by this build.");
        }

        if (_runner is null)
        {
            return Complete(organizationSlug, checks, _unavailable ?? "WSL cannot be reached.");
        }

        var found = await CheckEnvironmentAsync(_runner, profile, checks, ct);

        if (!found)
        {
            // Every remaining question is about the inside of a distribution that is not there.
            return Complete(
                organizationSlug,
                checks,
                $"the environment '{profile.EnvironmentId}' could not be examined.");
        }

        await CheckAgentIdentityAsync(_runner, profile, checks, ct);
        await CheckHostReachabilityAsync(_runner, profile, checks, ct);
        await CheckRootsAsync(_runner, profile, checks, ct);
        await CheckResourceControlAsync(_runner, profile, checks, ct);
        await CheckNetworkAsync(profile, checks, ct);

        return HardIsolationReadiness.From(organizationSlug, checks);
    }

    // ---------------------------------------------------------------------------------------
    // Environment
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the distribution exists and is a version 2 one, from the host side.
    ///
    /// Version matters and is not a formality: a version 1 distribution runs on a translation
    /// layer over the Windows filesystem, has no Linux kernel of its own and therefore no
    /// cgroups, so every resource limit this design depends on would silently not exist.
    /// </summary>
    private static async Task<bool> CheckEnvironmentAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var listed = await runner.RunAsync(["--list", "--verbose"], ct);

        if (!listed.Started)
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.EnvironmentExists, "WSL did not answer a request to list environments."));
            return false;
        }

        var entry = listed.Lines
            .Select(ParseListedEnvironment)
            .FirstOrDefault(e => e is not null && string.Equals(
                e.Value.Name, profile.EnvironmentId, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            checks.Add(ReadinessCheck.Failed(
                HardIsolationChecks.EnvironmentExists,
                $"No WSL environment named '{profile.EnvironmentId}' is registered."));
            return false;
        }

        checks.Add(ReadinessCheck.Passed(
            HardIsolationChecks.EnvironmentExists,
            $"The environment '{entry.Value.Name}' is registered."));

        checks.Add(entry.Value.Version == 2
            ? ReadinessCheck.Passed(
                HardIsolationChecks.EnvironmentVersion, "It is a WSL 2 environment.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.EnvironmentVersion,
                $"It is a WSL {entry.Value.Version} environment; only WSL 2 has a Linux kernel, and "
                + "without one there are no cgroups to enforce a limit with."));

        return true;
    }

    /// <summary>Reads one row of <c>wsl --list --verbose</c>, or nothing if the row is not one.</summary>
    private static (string Name, int Version)? ParseListedEnvironment(string line)
    {
        var trimmed = line.TrimStart('*', ' ', '\t');
        var fields = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length < 3
            || !int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            // The header row, and anything else that is not a registration.
            return null;
        }

        return (string.Join(' ', fields[..^2]), version);
    }

    // ---------------------------------------------------------------------------------------
    // Agent identity
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Who the agent is inside the environment, and what that identity can do.
    ///
    /// One call, because <c>id</c> reports the uid, the gid and every group at once, and three
    /// separate calls would be three chances for the answers to be about different moments.
    /// </summary>
    private static async Task CheckAgentIdentityAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var identity = await AsAgentAsync(runner, profile, [Id], ct);

        if (!identity.Ok)
        {
            var detail = identity.Started
                ? $"The environment has no usable account '{profile.AgentUser}'."
                : $"The environment could not be asked about the account '{profile.AgentUser}'.";

            checks.Add(identity.Started
                ? ReadinessCheck.Failed(HardIsolationChecks.AgentUserExists, detail)
                : ReadinessCheck.Unknown(HardIsolationChecks.AgentUserExists, detail));

            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.AgentUserNotRoot, "The agent's identity is not known."));
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.AgentUserNotPrivileged, "The agent's identity is not known."));
            return;
        }

        checks.Add(ReadinessCheck.Passed(
            HardIsolationChecks.AgentUserExists, $"The account '{profile.AgentUser}' exists."));

        var text = string.Join(' ', identity.Lines);
        var uid = ReadIdentityNumber(text, "uid=");
        var gid = ReadIdentityNumber(text, "gid=");

        if (uid is null || gid is null)
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.AgentUserNotRoot, "The environment reported no readable uid."));
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.AgentUserNotPrivileged, "The environment reported no readable groups."));
            return;
        }

        checks.Add(uid == 0 || gid == 0
            ? ReadinessCheck.Failed(
                HardIsolationChecks.AgentUserNotRoot,
                $"The agent runs as uid {uid}, gid {gid}; an agent that is root is not contained by anything.")
            : ReadinessCheck.Passed(
                HardIsolationChecks.AgentUserNotRoot, $"The agent runs as uid {uid}, gid {gid}."));

        var groups = ReadGroupNames(text);
        var privileged = groups.Where(PrivilegedGroups.Contains).Order(StringComparer.Ordinal).ToList();

        if (privileged.Count > 0)
        {
            checks.Add(ReadinessCheck.Failed(
                HardIsolationChecks.AgentUserNotPrivileged,
                $"The agent belongs to {string.Join(", ", privileged)}, which is a route out of its own "
                + "boundary; holding an unprivileged uid is not the same as being unprivileged."));

            return;
        }

        // Group membership is the usual way an account is granted administrative rights, and it
        // is not the only way: a line in sudoers can name a user directly, and that user belongs
        // to no group worth noticing. So the second question is asked of sudo itself.
        //
        // `-n -l` lists what the caller may do and runs none of it. A zero exit means the
        // listing succeeded without a password, which is elevation available on demand; anything
        // else - not a sudoer, a password required, no sudo installed at all - leaves the group
        // finding standing on its own.
        var elevation = await AsAgentAsync(runner, profile, [Sudo, "-n", "-l"], ct);

        checks.Add(elevation.Ok
            ? ReadinessCheck.Failed(
                HardIsolationChecks.AgentUserNotPrivileged,
                "The agent can elevate through sudo without a password, so it belongs to no "
                + "privileged group and is administrative anyway.")
            : ReadinessCheck.Passed(
                HardIsolationChecks.AgentUserNotPrivileged,
                $"The agent belongs to no privileged group ({groups.Count} group(s) examined) and "
                + "cannot elevate without a password."));
    }

    private static int? ReadIdentityNumber(string text, string prefix)
    {
        var start = text.IndexOf(prefix, StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        var digits = text.AsSpan(start + prefix.Length);
        var length = 0;

        while (length < digits.Length && char.IsAsciiDigit(digits[length]))
        {
            length++;
        }

        return length > 0
               && int.TryParse(digits[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Pulls the group names out of <c>groups=10(wheel),27(sudo)</c>.</summary>
    private static IReadOnlyList<string> ReadGroupNames(string text)
    {
        var start = text.IndexOf("groups=", StringComparison.Ordinal);

        if (start < 0)
        {
            return [];
        }

        var names = new List<string>();
        var span = text.AsSpan(start);

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != '(')
            {
                continue;
            }

            var end = span[i..].IndexOf(')');

            if (end > 1)
            {
                names.Add(span.Slice(i + 1, end - 1).ToString());
            }
        }

        return names;
    }

    // ---------------------------------------------------------------------------------------
    // Reachability
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What the environment can see that is not itself.
    ///
    /// Several checks off two observations, and that is on purpose. The mount table and the
    /// agent's PATH between them decide whether the host filesystem, the operator's provider
    /// credentials, the Hub's own database and a neighbouring environment are reachable - and
    /// each of those is a different consequence worth reporting under its own name, because an
    /// operator who reads "a drive is mounted" does not necessarily read "the agent can edit
    /// the record of its own run".
    /// </summary>
    private async Task CheckHostReachabilityAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var mounted = await AsServiceAsync(runner, profile, [Cat, "/proc/self/mountinfo"], ct);

        if (!mounted.Ok)
        {
            foreach (var id in new[]
                     {
                         HardIsolationChecks.HostDrivesNotMounted,
                         HardIsolationChecks.HostProviderAuthUnreachable,
                         HardIsolationChecks.ControlPlaneUnreachable,
                         HardIsolationChecks.OtherHardEnvironmentUnreachable,
                     })
            {
                checks.Add(ReadinessCheck.Unknown(id, "The environment's mount table could not be read."));
            }
        }
        else
        {
            AddMountChecks(WslMountTable.Parse(mounted.Lines), checks);
        }

        await CheckInteropAsync(runner, profile, checks, ct);
        await CheckPathAsync(runner, profile, checks, ct);
    }

    private void AddMountChecks(IReadOnlyList<MountEntry> entries, List<ReadinessCheck> checks)
    {
        var foreign = entries.Where(WslMountTable.IsForeign).ToList();

        // The read only driver share WSL mounts into every environment is a foreign filesystem
        // and is not a window onto the host: it carries no host volume, so it never appears in
        // any of the drive comparisons below and only ever shows up as a count.
        var hostVolumes = foreign.Where(e => e.HostDriveLetter is not null).ToList();

        checks.Add(hostVolumes.Count == 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.HostDrivesNotMounted,
                $"No host volume is mounted ({foreign.Count} non-native mount(s), none carrying a drive).")
            : ReadinessCheck.Failed(
                HardIsolationChecks.HostDrivesNotMounted,
                "The host filesystem is mounted at "
                + string.Join(", ", hostVolumes.Select(e => $"{e.MountPoint} ({e.FilesystemType})"))
                + "; automount must be off for a hard environment."));

        checks.Add(Reachable(hostVolumes, _hostProfileDrive, out var profileMount)
            ? ReadinessCheck.Failed(
                HardIsolationChecks.HostProviderAuthUnreachable,
                $"The host user profile volume is reachable at '{profileMount}', so provider credentials "
                + "stored on the host are reachable from inside the environment.")
            : ReadinessCheck.Passed(
                HardIsolationChecks.HostProviderAuthUnreachable,
                "The host user profile volume is not mounted, so host provider credentials cannot be read."));

        // Never a pass, and deliberately so.
        //
        // The question is whether a hard agent can reach the trusted Hub - which has no
        // multi-user authorisation boundary - and a filesystem observation cannot answer it. An
        // environment with no host volume mounted at all can still reach the Hub's API over
        // loopback forwarding, the host gateway or mirrored networking, and nothing in this
        // build probes for that. Reporting Pass here would be a configuration assumption
        // wearing the clothes of a measurement.
        //
        // So: a filesystem route, when one exists, is reported as the failure it is; the
        // absence of one is Unknown, which is not ready either. The network enforcement
        // mechanism, and the probe that can prove it, belong to a later slice.
        checks.Add(Reachable(hostVolumes, _controlPlaneDrive, out var controlMount)
            ? ReadinessCheck.Failed(
                HardIsolationChecks.ControlPlaneUnreachable,
                $"The volume holding the Hub's database and evidence is reachable at '{controlMount}'; "
                + "an agent that can reach it can edit the record of its own run.")
            : ReadinessCheck.Unknown(
                HardIsolationChecks.ControlPlaneUnreachable,
                "No filesystem route to the Hub's control plane was found, and whether one exists "
                + "over the network has not been established; this build has no probe for it."));

        // Reported as the shared roots rather than as every mount beneath them: WSL puts a
        // dozen bind mounts under those two, and naming all of them buries the finding.
        var shared = WslMountTable.CrossEnvironmentMountPoints
            .Where(root => entries.Any(
                e => e.MountPoint.Equals(root, StringComparison.Ordinal)
                     || e.MountPoint.StartsWith(root + "/", StringComparison.Ordinal)))
            .ToList();

        checks.Add(shared.Count == 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.OtherHardEnvironmentUnreachable,
                "No filesystem shared with another WSL environment is mounted.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.OtherHardEnvironmentUnreachable,
                $"{string.Join(", ", shared)} is shared by every environment on this WSL virtual machine, "
                + "so one organization's environment can pass data to another's."));
    }

    // ---------------------------------------------------------------------------------------
    // Network
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Adds what the network probe established, and lets the stricter answer win.
    ///
    /// The control plane is asked about twice on purpose, because there are two ways to reach it
    /// and each has its own evidence. The mount table answers whether there is a filesystem route
    /// to the Hub's own directory; the network probe answers whether there is a route over the
    /// network. Neither can speak for the other, so the two verdicts are combined by taking the
    /// worse - a Pass over the network does not undo a host volume being mounted, and a clean
    /// mount table does not make an open port unreachable.
    /// </summary>
    private async Task CheckNetworkAsync(
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var network = _network is null
            ? []
            : await _network.ProbeAsync(profile, ct);

        if (network.Count == 0)
        {
            foreach (var id in new[]
                     {
                         HardIsolationChecks.NetworkModeSupported,
                         HardIsolationChecks.NetworkPolicyEnforced,
                         HardIsolationChecks.HostLoopbackDisabled,
                         HardIsolationChecks.ProxyNotInherited,
                         HardIsolationChecks.PeerNetworkUnreachable,
                     })
            {
                checks.Add(ReadinessCheck.Unknown(id, "This build asked nothing about the network."));
            }

            return;
        }

        foreach (var check in network)
        {
            var existing = checks.FindIndex(c => string.Equals(c.Id, check.Id, StringComparison.Ordinal));

            if (existing < 0)
            {
                checks.Add(check);
                continue;
            }

            checks[existing] = Stricter(checks[existing], check);
        }
    }

    /// <summary>Fail beats Unknown beats Pass, and the losing detail is kept beside the winner.</summary>
    private static ReadinessCheck Stricter(ReadinessCheck first, ReadinessCheck second)
    {
        static int Weight(ReadinessStatus status) => status switch
        {
            ReadinessStatus.Fail => 2,
            ReadinessStatus.Unknown => 1,
            _ => 0,
        };

        var (winner, other) = Weight(first.Status) >= Weight(second.Status)
            ? (first, second)
            : (second, first);

        return winner with { Detail = winner.Detail + " " + other.Detail };
    }

    private static bool Reachable(IReadOnlyList<MountEntry> hostVolumes, char? drive, out string mountPoint)
    {
        mountPoint = string.Empty;

        if (drive is not { } letter)
        {
            return false;
        }

        var match = hostVolumes.FirstOrDefault(e => e.HostDriveLetter == letter);

        if (match is null)
        {
            return false;
        }

        mountPoint = match.MountPoint;
        return true;
    }

    /// <summary>
    /// Whether the environment can execute Windows programs directly.
    ///
    /// Interop is a binfmt registration, so the question is whether the kernel still has one.
    /// With it in place a mounted host volume is not even required: the environment can run a
    /// host executable, and an environment that can run host executables is not a boundary.
    /// </summary>
    private static async Task CheckInteropAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        string[] registrations =
            ["/proc/sys/fs/binfmt_misc/WSLInterop", "/proc/sys/fs/binfmt_misc/WSLInterop-late"];

        var probed = await AsServiceAsync(
            runner, profile, [Stat, "-c", "%n", ControlPath, .. registrations], ct);

        if (!Reached(probed))
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.HostInteropDisabled,
                "The environment could not be asked whether Windows interop is registered."));
            return;
        }

        // A missing path is reported on standard error and the others are still listed, so the
        // registrations that exist are exactly the lines that came back.
        var present = probed.Lines.Where(l => registrations.Contains(l, StringComparer.Ordinal)).ToList();

        checks.Add(present.Count == 0
            ? ReadinessCheck.Passed(
                HardIsolationChecks.HostInteropDisabled,
                "Windows interop is not registered, so host executables cannot be run.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.HostInteropDisabled,
                $"Windows interop is registered ({string.Join(", ", present)}), so the environment can "
                + "execute host programs directly."));
    }

    /// <summary>
    /// Whether the environment imports the host's PATH into the agent's.
    ///
    /// Measured with a marker rather than read out of configuration, and the marker is there
    /// because the obvious version of this check cannot fail. WSL appends the calling process's
    /// Windows PATH to the one inside; this probe is launched with almost no environment, so
    /// asking a default WSL environment for its PATH gets back a clean answer no matter how the
    /// environment is configured. Handing wsl.exe one synthetic entry - a directory that does
    /// not exist and never will - and looking for it inside makes the behaviour observable, and
    /// hands the environment nothing real if it is watching.
    ///
    /// The PATH that comes back is never reported either way. A machine with importing switched
    /// on returns the operator's entire Windows PATH, which names their installed software, their
    /// profile directory and their account; a count and a verdict answer the question without
    /// any of that ending up in a diagnostic.
    /// </summary>
    private static async Task CheckPathAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var path = await runner.RunAsync(
            ["-d", profile.EnvironmentId, "-u", profile.AgentUser, "--exec", Printenv, "PATH"],
            ct,
            hostPath: MarkerPath);

        if (!path.Ok)
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.HostPathNotImported, "The agent's PATH could not be read."));
            return;
        }

        var entries = string.Join(string.Empty, path.Lines)
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        var carried = entries.Count(e => e.Contains(PathMarker, StringComparison.OrdinalIgnoreCase));
        var outside = entries.Count(e => e.StartsWith("/mnt/", StringComparison.Ordinal));

        if (carried == 0 && outside == 0)
        {
            checks.Add(ReadinessCheck.Passed(
                HardIsolationChecks.HostPathNotImported,
                $"The host PATH is not imported; none of the agent's {entries.Length} entries came from "
                + "outside the environment."));
            return;
        }

        checks.Add(ReadinessCheck.Failed(
            HardIsolationChecks.HostPathNotImported,
            carried > 0
                ? "The environment appends the calling process's Windows PATH to the agent's, so the host "
                  + "toolchain is on the agent's PATH."
                : $"{outside} of the agent's {entries.Length} PATH entries point outside the environment."));
    }

    // ---------------------------------------------------------------------------------------
    // Roots
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the two directories the design depends on are what they say they are.
    ///
    /// The execution root has to be usable by the agent and the evidence root has to be
    /// unreadable by it, which is the asymmetry the whole evidence story rests on: a result the
    /// agent could edit is not evidence about the agent. Both have to be on a real Linux
    /// filesystem, because a path under a mounted host volume is a Linux name for a Windows
    /// directory and is neither contained nor governed by anything here.
    /// </summary>
    private static async Task CheckRootsAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var filesystems = await AsServiceAsync(
            runner,
            profile,
            [Stat, "-f", "-c", "%n|%T", ControlPath, profile.ExecutionRoot, profile.EvidenceRoot],
            ct);

        AddFilesystemCheck(
            checks, filesystems, profile.ExecutionRoot, HardIsolationChecks.ExecutionRootNative, "execution");
        AddFilesystemCheck(
            checks, filesystems, profile.EvidenceRoot, HardIsolationChecks.EvidenceRootNative, "evidence");

        var described = await AsServiceAsync(
            runner,
            profile,
            [Stat, "-c", "%n|%A|%U|%G|%a", ControlPath, profile.ExecutionRoot, profile.EvidenceRoot],
            ct);

        var writable = await AsAgentAsync(runner, profile, [Test, "-w", profile.ExecutionRoot], ct);

        AddExecutionRootCheck(checks, described, writable, profile);

        var readable = await AsAgentAsync(runner, profile, [Test, "-r", profile.EvidenceRoot], ct);

        AddEvidenceRootCheck(checks, described, readable, profile);
    }

    private static void AddFilesystemCheck(
        List<ReadinessCheck> checks,
        WslResult result,
        string root,
        string id,
        string role)
    {
        if (!Reached(result))
        {
            checks.Add(ReadinessCheck.Unknown(id, $"The {role} root's filesystem could not be identified."));
            return;
        }

        var type = FieldFor(result, root, index: 1);

        if (type is null)
        {
            checks.Add(ReadinessCheck.Failed(
                id, $"The {role} root '{root}' does not exist inside the environment."));
            return;
        }

        checks.Add(WslMountTable.NativeFilesystems.Contains(type)
            ? ReadinessCheck.Passed(id, $"The {role} root is on a native '{type}' filesystem.")
            : ReadinessCheck.Failed(
                id,
                $"The {role} root is on a '{type}' filesystem, which is not native to the environment; "
                + "a hard environment stores nothing outside itself."));
    }

    private static void AddExecutionRootCheck(
        List<ReadinessCheck> checks,
        WslResult described,
        WslResult writable,
        HardIsolationProfile profile)
    {
        if (!Reached(described) || !Answered(writable))
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.ExecutionRootUsable, "The execution root could not be examined."));
            return;
        }

        // %A, whose first character is the file type, so this is both the existence and the
        // "is it actually a directory" answer.
        var access = FieldFor(described, profile.ExecutionRoot, index: 1);

        if (access is null || !access.StartsWith('d'))
        {
            checks.Add(ReadinessCheck.Failed(
                HardIsolationChecks.ExecutionRootUsable,
                $"The execution root '{profile.ExecutionRoot}' is not a directory inside the environment."));
            return;
        }

        checks.Add(writable.Ok
            ? ReadinessCheck.Passed(
                HardIsolationChecks.ExecutionRootUsable,
                "The agent can write to the execution root (owner "
                + $"{FieldFor(described, profile.ExecutionRoot, 2)}, "
                + $"mode {FieldFor(described, profile.ExecutionRoot, 4)}).")
            : ReadinessCheck.Failed(
                HardIsolationChecks.ExecutionRootUsable,
                $"The agent cannot write to the execution root '{profile.ExecutionRoot}'."));
    }

    private static void AddEvidenceRootCheck(
        List<ReadinessCheck> checks,
        WslResult described,
        WslResult readable,
        HardIsolationProfile profile)
    {
        if (!Reached(described) || !Answered(readable))
        {
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.EvidenceRootNotAgentReadable,
                "The evidence root could not be examined."));
            return;
        }

        var owner = FieldFor(described, profile.EvidenceRoot, index: 2);
        var mode = FieldFor(described, profile.EvidenceRoot, index: 4);

        if (owner is null)
        {
            checks.Add(ReadinessCheck.Failed(
                HardIsolationChecks.EvidenceRootNotAgentReadable,
                $"The evidence root '{profile.EvidenceRoot}' does not exist inside the environment."));
            return;
        }

        if (readable.Ok)
        {
            checks.Add(ReadinessCheck.Failed(
                HardIsolationChecks.EvidenceRootNotAgentReadable,
                $"The agent can read the evidence root (owner {owner}, mode {mode}); evidence the observed "
                + "party can read is not evidence about it."));
            return;
        }

        // Two conditions, and both are needed. The agent failing to read it today could be an
        // accident of one directory's mode; the service user owning it is what makes the
        // arrangement deliberate rather than lucky.
        checks.Add(string.Equals(owner, profile.ServiceUser, StringComparison.Ordinal)
            ? ReadinessCheck.Passed(
                HardIsolationChecks.EvidenceRootNotAgentReadable,
                $"The evidence root is owned by '{owner}' with mode {mode} and the agent cannot read it.")
            : ReadinessCheck.Failed(
                HardIsolationChecks.EvidenceRootNotAgentReadable,
                $"The evidence root is owned by '{owner}' rather than the service user "
                + $"'{profile.ServiceUser}', so the agent being unable to read it today is incidental."));
    }

    /// <summary>Whether a batched <c>stat</c> actually ran inside the environment.</summary>
    private static bool Reached(WslResult result) =>
        result.Started && WslStatResponse.Reached(result.Lines);

    /// <summary>
    /// Whether a <c>test</c> invocation was answered by the environment rather than by WSL.
    ///
    /// <c>test</c> exits 0 or 1 and nothing else, so any other code is wsl.exe reporting its
    /// own failure - which is not an answer about the path that was asked about.
    /// </summary>
    private static bool Answered(WslResult result) =>
        result.Started && result.ExitCode is 0 or 1;

    /// <summary>Finds the row a batched <c>stat</c> produced for one path, and takes one field.</summary>
    private static string? FieldFor(WslResult result, string path, int index) =>
        WslStatResponse.Field(result.Lines, path, index);

    // ---------------------------------------------------------------------------------------
    // Resource control
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the environment can actually enforce a limit.
    ///
    /// Declaring a ceiling is configuration; enforcing one needs systemd to place work in a
    /// scope, cgroup v2 to hold the accounting, and the memory and pids controllers to be
    /// delegated to it. Any of those missing means the ceilings in the profile are numbers in a
    /// file - which is why this is measured now rather than discovered by the execution plane
    /// later.
    /// </summary>
    private static async Task CheckResourceControlAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        List<ReadinessCheck> checks,
        CancellationToken ct)
    {
        var systemd = await AsServiceAsync(runner, profile, [Systemctl, "is-system-running"], ct);

        var state = systemd.Started ? systemd.Lines.FirstOrDefault() ?? string.Empty : string.Empty;

        if (!SystemdStates.Contains(state))
        {
            // Not a failure of systemd: a launch that never reached the environment also
            // produces a line of text, and this is how the two are told apart.
            checks.Add(ReadinessCheck.Unknown(
                HardIsolationChecks.SystemdAvailable, "The environment could not be asked about systemd."));
        }
        else
        {
            // "degraded" is a running manager with a failed unit somewhere. It still places
            // scopes and still enforces limits, so it is not a reason to refuse an execution.
            checks.Add(state is "running" or "degraded" or "starting"
                ? ReadinessCheck.Passed(HardIsolationChecks.SystemdAvailable, $"systemd reports '{state}'.")
                : ReadinessCheck.Failed(
                    HardIsolationChecks.SystemdAvailable,
                    $"systemd reports '{state}'; without a running manager there is no scope to place a "
                    + "limited execution in."));
        }

        var cgroup = await AsServiceAsync(runner, profile, [Stat, "-f", "-c", "%T", "/sys/fs/cgroup"], ct);
        var version = cgroup.Ok ? cgroup.Lines.FirstOrDefault() : null;

        checks.Add(version switch
        {
            null => ReadinessCheck.Unknown(
                HardIsolationChecks.CgroupV2Available, "The cgroup filesystem could not be identified."),
            "cgroup2fs" => ReadinessCheck.Passed(
                HardIsolationChecks.CgroupV2Available, "cgroup v2 is mounted at /sys/fs/cgroup."),
            _ => ReadinessCheck.Failed(
                HardIsolationChecks.CgroupV2Available,
                $"/sys/fs/cgroup is a '{version}' filesystem rather than cgroup v2."),
        });

        var controllers = await AsServiceAsync(
            runner, profile, [Cat, "/sys/fs/cgroup/cgroup.controllers"], ct);

        var available = controllers.Ok
            ? string.Join(' ', controllers.Lines).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : null;

        AddControllerCheck(checks, available, "memory", HardIsolationChecks.MemoryControllerAvailable);
        AddControllerCheck(checks, available, "pids", HardIsolationChecks.PidsControllerAvailable);
    }

    private static void AddControllerCheck(
        List<ReadinessCheck> checks,
        string[]? available,
        string controller,
        string id)
    {
        if (available is null)
        {
            checks.Add(ReadinessCheck.Unknown(id, "The delegated cgroup controllers could not be read."));
            return;
        }

        checks.Add(available.Contains(controller, StringComparer.Ordinal)
            ? ReadinessCheck.Passed(id, $"The '{controller}' controller is delegated.")
            : ReadinessCheck.Failed(
                id,
                $"The '{controller}' controller is not delegated, so the declared ceiling cannot be enforced."));
    }

    // ---------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------

    private static Task<WslResult> AsAgentAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        IReadOnlyList<string> command,
        CancellationToken ct) =>
        runner.RunAsync(["-d", profile.EnvironmentId, "-u", profile.AgentUser, "--exec", .. command], ct);

    private static Task<WslResult> AsServiceAsync(
        IWslCommandRunner runner,
        HardIsolationProfile profile,
        IReadOnlyList<string> command,
        CancellationToken ct) =>
        runner.RunAsync(["-d", profile.EnvironmentId, "-u", profile.ServiceUser, "--exec", .. command], ct);

    /// <summary>Answers everything still unasked with the reason it was not asked.</summary>
    private static HardIsolationReadiness Complete(
        string organizationSlug,
        List<ReadinessCheck> checks,
        string detail)
    {
        var answered = checks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        checks.AddRange(HardIsolationChecks.Required
            .Where(id => !answered.Contains(id))
            .Select(id => ReadinessCheck.Unknown(id, $"Not established, because {detail}")));

        return HardIsolationReadiness.From(organizationSlug, checks);
    }

    private static char? DriveLetterOf(string path)
    {
        var root = string.IsNullOrWhiteSpace(path) ? null : Path.GetPathRoot(Path.GetFullPath(path));

        return root is { Length: > 1 } && char.IsAsciiLetter(root[0]) && root[1] == ':'
            ? char.ToUpperInvariant(root[0])
            : null;
    }
}
