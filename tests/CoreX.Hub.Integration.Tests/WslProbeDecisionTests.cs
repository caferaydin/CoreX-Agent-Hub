using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Every readiness judgement, against an environment whose answers the test writes.
///
/// The real machine test beside this one proves the probe can ask a live distribution these
/// questions. What it cannot prove is what the probe concludes from an answer it will never see
/// on a healthy laptop - an agent in the sudo group, an evidence directory the agent can read, a
/// kernel with no pids controller - because arranging any of those means damaging the operator's
/// machine, which this phase forbids.
///
/// Each test below starts from a correct environment and degrades exactly one answer. That shape
/// matters: a check that failed for the wrong reason would show up as two failures rather than
/// one, and a check that cannot fail at all would show up as none.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslProbeDecisionTests
{
    private static ReadinessStatus Status(HardIsolationReadiness readiness, string id) =>
        readiness.Checks.Single(c => c.Id == id).Status;

    private static async Task<ReadinessStatus> StatusOf(ScriptedWslEnvironment script, string id) =>
        Status(await script.ReadinessAsync(), id);

    /// <summary>
    /// A correct environment passes everything this script can answer - and is still not ready.
    ///
    /// Both halves are the point. Every check the command runner can satisfy is satisfied, so none
    /// of the failures below is an artefact of a script that was wrong to begin with; and what
    /// remains unmet is the network, which this scripted environment has no probe for.
    ///
    /// That the network questions cannot be answered from a command script is not a gap in the
    /// script. They are about reachability from the agent's own account, and the only honest way
    /// to answer them is to ask the operating system - which is why an absent probe reports
    /// unknown here rather than nothing, and why unknown keeps a perfect environment out.
    /// </summary>
    [Fact]
    public async Task A_correct_environment_passes_everything_except_the_network()
    {
        var readiness = await ScriptedWslEnvironment.Correct().ReadinessAsync();

        string[] network =
        [
            HardIsolationChecks.ControlPlaneUnreachable,
            HardIsolationChecks.NetworkModeSupported,
            HardIsolationChecks.NetworkPolicyEnforced,
            HardIsolationChecks.HostLoopbackDisabled,
            HardIsolationChecks.ProxyNotInherited,
            HardIsolationChecks.PeerNetworkUnreachable,
        ];

        Assert.Equal(
            network.Order(StringComparer.Ordinal),
            readiness.UnmetCheckIds.Order(StringComparer.Ordinal));

        foreach (var id in network)
        {
            Assert.Equal(ReadinessStatus.Unknown, Status(readiness, id));
        }

        Assert.False(readiness.IsReady);
    }

    // -- environment ------------------------------------------------------------------------

    [Fact]
    public async Task An_environment_that_is_not_registered_fails_and_stops_the_probe()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Host("--list --verbose", "  NAME       STATE     VERSION\n* Ubuntu     Running   2");

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.EnvironmentExists));

        // Nothing inside a distribution that is not there can be measured, and none of it is
        // reported as a failure of the environment either.
        Assert.Equal(ReadinessStatus.Unknown, Status(readiness, HardIsolationChecks.AgentUserExists));
        Assert.Equal(ReadinessStatus.Unknown, Status(readiness, HardIsolationChecks.SystemdAvailable));
    }

    /// <summary>
    /// A version 1 environment is refused.
    ///
    /// Not a formality: version 1 runs on a translation layer over the Windows filesystem and has
    /// no Linux kernel of its own, so every cgroup limit this design depends on would silently
    /// not exist.
    /// </summary>
    [Fact]
    public async Task A_version_one_environment_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Host("--list --verbose", "  NAME            STATE     VERSION\n* corex-fixture   Running   1");

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.EnvironmentVersion));
    }

    // -- agent identity ---------------------------------------------------------------------

    [Fact]
    public async Task An_agent_running_as_root_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Agent("/usr/bin/id", "uid=0(root) gid=0(root) groups=0(root)");

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotRoot));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotPrivileged));
    }

    /// <summary>
    /// The check the whole design leans on: an unprivileged uid is not the same as unprivileged.
    ///
    /// This is the default Ubuntu account, and it becomes root whenever it asks.
    /// </summary>
    [Theory]
    [InlineData("uid=1000(a) gid=1000(a) groups=1000(a),27(sudo)")]
    [InlineData("uid=1000(a) gid=1000(a) groups=1000(a),10(wheel)")]
    [InlineData("uid=1000(a) gid=1000(a) groups=1000(a),4(adm)")]
    [InlineData("uid=1000(a) gid=1000(a) groups=1000(a),999(docker)")]
    [InlineData("uid=1000(a) gid=1000(a) groups=1000(a),42(shadow)")]
    public async Task An_agent_in_a_privileged_group_fails_even_with_an_ordinary_uid(string identity)
    {
        var script = ScriptedWslEnvironment.Correct().Agent("/usr/bin/id", identity);

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.AgentUserNotRoot));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotPrivileged));
    }

    /// <summary>
    /// Group membership is not the only way an account is given administrative rights.
    ///
    /// A line in sudoers can name a user directly, and that user belongs to no group worth
    /// noticing. Here the groups are clean and the account can still become root on demand,
    /// which is the case a group-only check would wave through.
    /// </summary>
    [Fact]
    public async Task An_agent_that_can_elevate_without_a_password_fails_despite_clean_groups()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Agent("/usr/bin/sudo -n -l", "User corex-agent may run the following commands:\n    (ALL) NOPASSWD: ALL");

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.AgentUserNotRoot));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotPrivileged));
    }

    /// <summary>
    /// An account that is a sudoer but must type a password is still reported as privileged.
    ///
    /// Only because it is in the sudo group: sudo itself exits non-zero here, so the group
    /// finding is what stands - which is the right answer, and the reason both questions are
    /// asked rather than either alone.
    /// </summary>
    [Fact]
    public async Task A_sudoer_who_must_type_a_password_is_still_privileged()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Agent("/usr/bin/id", "uid=1000(a) gid=1000(a) groups=1000(a),27(sudo)")
            .Agent("/usr/bin/sudo -n -l", "sudo: interactive authentication is required", exitCode: 1);

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.AgentUserNotPrivileged));
    }

    [Fact]
    public async Task An_agent_account_that_does_not_exist_fails()
    {
        var script = ScriptedWslEnvironment.Correct().Agent("/usr/bin/id", string.Empty, exitCode: 1);

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserExists));
        Assert.Equal(ReadinessStatus.Unknown, Status(readiness, HardIsolationChecks.AgentUserNotRoot));
    }

    // -- reachability -----------------------------------------------------------------------

    [Fact]
    public async Task A_mounted_host_drive_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /proc/self/mountinfo", ScriptedWslEnvironment.HostExposedMounts);

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.HostDrivesNotMounted));
    }

    /// <summary>
    /// The consequences of one mount, reported under the names an operator can act on.
    ///
    /// The drives are supplied so the two volume comparisons have something to match: this is a
    /// machine whose user profile and whose Hub database both live on C:.
    /// </summary>
    [Fact]
    public async Task A_mounted_host_drive_exposes_provider_credentials_and_the_control_plane()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /proc/self/mountinfo", ScriptedWslEnvironment.HostExposedMounts);

        var readiness = await script.ReadinessAsync(controlPlaneDrive: 'C', hostProfileDrive: 'C');

        Assert.Equal(
            ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.HostProviderAuthUnreachable));
        Assert.Equal(
            ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.ControlPlaneUnreachable));
    }

    /// <summary>
    /// A volume that is mounted but is not the one in question does not fail these checks.
    ///
    /// Without this, the two checks above would pass their tests simply by failing whenever any
    /// drive is mounted, and would tell an operator nothing about which one matters.
    /// </summary>
    [Fact]
    public async Task A_mounted_drive_that_holds_neither_is_not_reported_as_either()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /proc/self/mountinfo", ScriptedWslEnvironment.HostExposedMounts);

        var readiness = await script.ReadinessAsync(controlPlaneDrive: 'E', hostProfileDrive: 'E');

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.HostDrivesNotMounted));
        Assert.Equal(
            ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.HostProviderAuthUnreachable));
    }

    /// <summary>
    /// The filesystem every WSL environment on one virtual machine shares.
    ///
    /// Not a host volume and not a Windows path, which is what makes it easy to overlook: it is a
    /// tmpfs two distributions can both write to, so one organization's environment can hand
    /// something to another's through it.
    /// </summary>
    [Fact]
    public async Task A_filesystem_shared_with_another_environment_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /proc/self/mountinfo", ScriptedWslEnvironment.HostExposedMounts);

        Assert.Equal(
            ReadinessStatus.Fail,
            await StatusOf(script, HardIsolationChecks.OtherHardEnvironmentUnreachable));
    }

    [Fact]
    public async Task A_registered_windows_interop_handler_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                "/usr/bin/stat -c %n /proc/version /proc/sys/fs/binfmt_misc/WSLInterop "
                + "/proc/sys/fs/binfmt_misc/WSLInterop-late",
                "/proc/version\n/proc/sys/fs/binfmt_misc/WSLInterop",
                exitCode: 1);

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.HostInteropDisabled));
    }

    /// <summary>
    /// The marker comes back, so the environment appends the caller's Windows PATH.
    ///
    /// This is what makes the check able to fail at all: the probe runs with almost no
    /// environment, so asking a default distribution for its PATH would otherwise return a clean
    /// answer however it is configured.
    /// </summary>
    [Fact]
    public async Task An_environment_that_imports_the_host_path_fails()
    {
        var script = ScriptedWslEnvironment.Correct().ImportsTheHostPath();

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.HostPathNotImported));
    }

    // -- roots ------------------------------------------------------------------------------

    /// <summary>
    /// A root on a mounted host volume is a Linux name for a Windows directory.
    ///
    /// Nothing about it is contained or governed by the environment, whatever the path looks
    /// like - which is why the filesystem type is measured rather than the name inspected.
    /// </summary>
    [Theory]
    [InlineData("9p")]
    [InlineData("drvfs")]
    [InlineData("cifs")]
    public async Task A_root_that_is_not_on_a_native_filesystem_fails(string filesystem)
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                $"/usr/bin/stat -f -c %n|%T /proc/version {ScriptedWslEnvironment.ExecutionRoot} "
                + ScriptedWslEnvironment.EvidenceRoot,
                $"/proc/version|proc\n{ScriptedWslEnvironment.ExecutionRoot}|{filesystem}\n"
                + $"{ScriptedWslEnvironment.EvidenceRoot}|{filesystem}");

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.ExecutionRootNative));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.EvidenceRootNative));
    }

    [Fact]
    public async Task A_root_that_does_not_exist_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                $"/usr/bin/stat -f -c %n|%T /proc/version {ScriptedWslEnvironment.ExecutionRoot} "
                + ScriptedWslEnvironment.EvidenceRoot,
                "/proc/version|proc",
                exitCode: 1);

        var readiness = await script.ReadinessAsync();

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.ExecutionRootNative));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.EvidenceRootNative));
    }

    [Fact]
    public async Task An_execution_root_the_agent_cannot_write_to_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Agent($"/usr/bin/test -w {ScriptedWslEnvironment.ExecutionRoot}", string.Empty, exitCode: 1);

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.ExecutionRootUsable));
    }

    [Fact]
    public async Task An_execution_root_that_is_not_a_directory_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                $"/usr/bin/stat -c %n|%A|%U|%G|%a /proc/version {ScriptedWslEnvironment.ExecutionRoot} "
                + ScriptedWslEnvironment.EvidenceRoot,
                "/proc/version|-r--r--r--|root|root|444\n"
                + $"{ScriptedWslEnvironment.ExecutionRoot}|-rw-rw----|corex-hub|corex-agent|660\n"
                + $"{ScriptedWslEnvironment.EvidenceRoot}|drwx------|corex-hub|corex-hub|700");

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.ExecutionRootUsable));
    }

    /// <summary>
    /// Evidence the observed party can read is not evidence about it.
    ///
    /// The asymmetry between the two roots is what the whole evidence story rests on, so an
    /// evidence directory the agent can open is a failure however correct everything else is.
    /// </summary>
    [Fact]
    public async Task An_evidence_root_the_agent_can_read_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Agent($"/usr/bin/test -r {ScriptedWslEnvironment.EvidenceRoot}", string.Empty);

        Assert.Equal(
            ReadinessStatus.Fail,
            await StatusOf(script, HardIsolationChecks.EvidenceRootNotAgentReadable));
    }

    /// <summary>
    /// An evidence directory the agent happens not to be able to read is not enough.
    ///
    /// Ownership is what makes the arrangement deliberate and survivable; without it, one
    /// permission change - by the agent's own owner - undoes it silently.
    /// </summary>
    [Fact]
    public async Task An_evidence_root_the_agent_owns_fails_even_when_it_cannot_be_read()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                $"/usr/bin/stat -c %n|%A|%U|%G|%a /proc/version {ScriptedWslEnvironment.ExecutionRoot} "
                + ScriptedWslEnvironment.EvidenceRoot,
                "/proc/version|-r--r--r--|root|root|444\n"
                + $"{ScriptedWslEnvironment.ExecutionRoot}|drwxrwx---|corex-hub|corex-agent|770\n"
                + $"{ScriptedWslEnvironment.EvidenceRoot}|drwx------|corex-agent|corex-agent|700");

        Assert.Equal(
            ReadinessStatus.Fail,
            await StatusOf(script, HardIsolationChecks.EvidenceRootNotAgentReadable));
    }

    // -- resource control -------------------------------------------------------------------

    [Theory]
    [InlineData("offline")]
    [InlineData("stopping")]
    public async Task An_environment_without_a_running_systemd_fails(string state)
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/systemctl is-system-running", state, exitCode: 1);

        Assert.Equal(ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.SystemdAvailable));
    }

    /// <summary>
    /// A manager with a failed unit somewhere still places scopes and still enforces limits.
    ///
    /// Refusing an execution for it would make the gate report a problem it does not have.
    /// </summary>
    [Fact]
    public async Task A_degraded_systemd_still_passes()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/systemctl is-system-running", "degraded", exitCode: 1);

        Assert.Equal(ReadinessStatus.Pass, await StatusOf(script, HardIsolationChecks.SystemdAvailable));
    }

    /// <summary>Text that is not systemd's own vocabulary is not an answer about systemd.</summary>
    [Fact]
    public async Task An_unrecognisable_systemd_answer_is_unknown_rather_than_a_failure()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service(
                "/usr/bin/systemctl is-system-running",
                "The RPC call contains a handle that differs from the declared handle type",
                exitCode: 1);

        Assert.Equal(
            ReadinessStatus.Unknown, await StatusOf(script, HardIsolationChecks.SystemdAvailable));
    }

    [Fact]
    public async Task An_environment_without_cgroup_v2_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/stat -f -c %T /sys/fs/cgroup", "tmpfs");

        Assert.Equal(ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.CgroupV2Available));
    }

    [Fact]
    public async Task An_undelegated_memory_controller_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /sys/fs/cgroup/cgroup.controllers", "cpuset cpu io pids");

        var readiness = await script.ReadinessAsync();

        Assert.Equal(
            ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.MemoryControllerAvailable));
        Assert.Equal(
            ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.PidsControllerAvailable));
    }

    [Fact]
    public async Task An_undelegated_pids_controller_fails()
    {
        var script = ScriptedWslEnvironment.Correct()
            .Service("/usr/bin/cat /sys/fs/cgroup/cgroup.controllers", "cpuset cpu io memory");

        Assert.Equal(
            ReadinessStatus.Fail, await StatusOf(script, HardIsolationChecks.PidsControllerAvailable));
    }

    /// <summary>
    /// A command the environment never answered is Unknown, not a finding.
    ///
    /// The distinction is the difference between a diagnostic that reports what it saw and one
    /// that reports faults it invented in a distribution that is fine.
    /// </summary>
    [Fact]
    public async Task A_command_the_environment_never_answered_is_unknown()
    {
        // The script has no answer for the mount table, so the runner reports it as not started.
        var script = ScriptedWslEnvironment.Correct();
        script.Service("/usr/bin/cat /proc/self/mountinfo", string.Empty, exitCode: 1);

        var readiness = await script.ReadinessAsync();

        Assert.Equal(
            ReadinessStatus.Unknown, Status(readiness, HardIsolationChecks.HostDrivesNotMounted));
        Assert.Equal(
            ReadinessStatus.Unknown,
            Status(readiness, HardIsolationChecks.OtherHardEnvironmentUnreachable));
    }
}
