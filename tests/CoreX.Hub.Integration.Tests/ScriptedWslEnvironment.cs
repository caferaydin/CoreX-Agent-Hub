using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// A WSL environment whose every answer the test writes.
///
/// It exists because the readiness decisions worth being certain about cannot be arranged on a
/// real machine without damaging it. An environment whose agent sits in the sudo group, whose
/// evidence directory the agent can read, or whose kernel has no pids controller is exactly what
/// these checks are for - and creating one would mean editing the operator's distribution,
/// which this phase forbids outright.
///
/// So the seam is the command runner, and nothing above it is substituted: the real probe, the
/// real parsing and the real judgements all run. The integration suite proves separately that
/// the shipped runner asks a real environment these same questions.
///
/// The script starts as a correct environment and each test degrades exactly one answer, so a
/// failing check is always attributable to the thing the test changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScriptedWslEnvironment : IWslCommandRunner
{
    public const string EnvironmentId = "corex-fixture";
    public const string AgentUser = "corex-agent";
    public const string ServiceUser = "corex-hub";
    public const string ExecutionRoot = "/srv/corex/exec";
    public const string EvidenceRoot = "/srv/corex/evidence";

    /// <summary>A mount table with nothing in it but the environment's own filesystems.</summary>
    public const string NativeMounts =
        """
        82 67 8:48 / / rw,relatime - ext4 /dev/sdd rw,discard
        114 82 0:61 / /proc rw,nosuid,nodev,noexec,noatime shared:16 - proc proc rw
        122 113 0:25 / /sys/fs/cgroup rw,nosuid,nodev,noexec,relatime shared:23 - cgroup2 cgroup2 rw
        """;

    /// <summary>The same table on a default WSL distribution: the host is a directory away.</summary>
    public const string HostExposedMounts =
        """
        82 67 8:48 / / rw,relatime - ext4 /dev/sdd rw,discard
        78 82 0:34 / /mnt/wsl rw,relatime shared:1 - tmpfs none rw
        122 113 0:25 / /sys/fs/cgroup rw,nosuid,nodev,noexec,relatime shared:23 - cgroup2 cgroup2 rw
        132 82 0:70 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=C:\
        """;

    private readonly Dictionary<string, WslResult> _answers = new(StringComparer.Ordinal);

    private ScriptedWslEnvironment()
    {
    }

    /// <summary>An environment that satisfies every check this build is able to satisfy.</summary>
    public static ScriptedWslEnvironment Correct()
    {
        var script = new ScriptedWslEnvironment();

        script.Host("--list --verbose", "  NAME            STATE     VERSION\n* corex-fixture   Running   2");

        script.Agent("/usr/bin/id", "uid=1000(corex-agent) gid=1000(corex-agent) groups=1000(corex-agent)");
        script.Agent("/usr/bin/printenv PATH", "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
        script.Agent($"/usr/bin/test -w {ExecutionRoot}", string.Empty);

        // Not a sudoer: sudo says so and exits non-zero.
        script.Agent("/usr/bin/sudo -n -l", "Sorry, user corex-agent may not run sudo.", exitCode: 1);

        // The agent cannot read the evidence root, which `test` reports by exiting 1.
        script.Agent($"/usr/bin/test -r {EvidenceRoot}", string.Empty, exitCode: 1);

        script.Service("/usr/bin/cat /proc/self/mountinfo", NativeMounts);

        // Neither interop registration exists, so stat lists nothing and exits non-zero.
        script.Service(
            "/usr/bin/stat -c %n /proc/version /proc/sys/fs/binfmt_misc/WSLInterop "
            + "/proc/sys/fs/binfmt_misc/WSLInterop-late",
            "/proc/version",
            exitCode: 1);

        script.Service(
            $"/usr/bin/stat -f -c %n|%T /proc/version {ExecutionRoot} {EvidenceRoot}",
            $"/proc/version|proc\n{ExecutionRoot}|ext2/ext3\n{EvidenceRoot}|ext2/ext3");

        script.Service(
            $"/usr/bin/stat -c %n|%A|%U|%G|%a /proc/version {ExecutionRoot} {EvidenceRoot}",
            "/proc/version|-r--r--r--|root|root|444\n"
            + $"{ExecutionRoot}|drwxrwx---|corex-hub|corex-agent|770\n"
            + $"{EvidenceRoot}|drwx------|corex-hub|corex-hub|700");

        // Inside the boundary, every shared surface is refused and the workspace still works.
        foreach (var surface in HardFilesystemPolicy.Phase10.DeniedSurfaces)
        {
            script.Sandboxed($"/usr/bin/test -r {surface.Path}", exitCode: 1);
            script.Sandboxed($"/usr/bin/test -w {surface.Path}", exitCode: 1);
        }

        script.Sandboxed($"/usr/bin/test -w {ExecutionRoot}");
        script.Sandboxed("/usr/bin/cat /proc/self/mountinfo", NativeMounts);

        script.Service("/usr/bin/systemctl is-system-running", "running");
        script.Service("/usr/bin/stat -f -c %T /sys/fs/cgroup", "cgroup2fs");
        script.Service("/usr/bin/cat /sys/fs/cgroup/cgroup.controllers", "cpuset cpu io memory hugetlb pids rdma");

        return script;
    }

    /// <summary>Replaces what the environment answers when asked as the agent.</summary>
    public ScriptedWslEnvironment Agent(string command, string stdout, int exitCode = 0)
    {
        _answers[$"-d {EnvironmentId} -u {AgentUser} --exec {command}"] =
            new WslResult(true, exitCode, stdout, string.Empty);

        return this;
    }

    /// <summary>Replaces what the environment answers when asked as the service user.</summary>
    public ScriptedWslEnvironment Service(string command, string stdout, int exitCode = 0)
    {
        _answers[$"-d {EnvironmentId} -u {ServiceUser} --exec {command}"] =
            new WslResult(true, exitCode, stdout, string.Empty);

        return this;
    }

    /// <summary>
    /// Replaces what the environment answers when asked from inside the real boundary.
    ///
    /// The signature is built from <see cref="HardMountSandbox"/> rather than written out, so a
    /// script cannot keep answering a launch shape the probe has stopped using - the tests would
    /// go Unknown, which is never a pass, rather than silently keep passing.
    /// </summary>
    public ScriptedWslEnvironment Sandboxed(string command, string stdout = "", int exitCode = 0)
    {
        var properties = string.Join(
            ' ', HardMountSandbox.Properties(HardFilesystemPolicy.Phase10).Select(p => "--property=" + p));

        _answers[
                $"-d {EnvironmentId} -u root --exec /usr/bin/systemd-run --quiet --pipe --wait --collect "
                + $"--uid={AgentUser} {properties} -- {command}"] =
            new WslResult(true, exitCode, stdout, string.Empty);

        return this;
    }

    /// <summary>
    /// Forgets one scripted answer, so the environment stops responding to that command.
    ///
    /// Matched on two fragments rather than on the whole signature, because the sandbox properties
    /// in the middle belong to the shipped boundary and a test that spelled them would be a test
    /// that had to be edited every time the boundary changed.
    /// </summary>
    public ScriptedWslEnvironment Unanswered(string containing, string alsoContaining)
    {
        foreach (var key in _answers.Keys
                     .Where(k => k.Contains(containing, StringComparison.Ordinal)
                                 && k.Contains(alsoContaining, StringComparison.Ordinal))
                     .ToList())
        {
            _answers.Remove(key);
        }

        return this;
    }

    public ScriptedWslEnvironment Host(string command, string stdout, int exitCode = 0)
    {
        _answers[command] = new WslResult(true, exitCode, stdout, string.Empty);
        return this;
    }

    /// <summary>Builds the probe over this script.</summary>
    public WslReadinessProbe Probe(char? controlPlaneDrive = null, char? hostProfileDrive = null) =>
        WslReadinessProbe.Scripted(this, controlPlaneDrive, hostProfileDrive);

    public async Task<HardIsolationReadiness> ReadinessAsync(
        char? controlPlaneDrive = null,
        char? hostProfileDrive = null) =>
        await Probe(controlPlaneDrive, hostProfileDrive)
            .ProbeAsync("fixture", Profile(), CancellationToken.None);

    /// <summary>The profile every scripted test uses. It parses; it says nothing about a machine.</summary>
    public static HardIsolationProfile Profile()
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = EnvironmentId,
            AgentUser = AgentUser,
            ServiceUser = ServiceUser,
            ExecutionRoot = ExecutionRoot,
            EvidenceRoot = EvidenceRoot,
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    /// <summary>
    /// Answers what the script was told to answer, and nothing else.
    ///
    /// A command the script has no answer for is reported as not started rather than as an
    /// empty success, so a test that forgot to script something sees Unknown - which is never a
    /// pass, and is exactly how the production probe treats a command it could not run.
    /// </summary>
    public Task<WslResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct, string? hostPath = null)
    {
        var signature = string.Join(' ', arguments);

        // The PATH question is asked with a synthetic PATH so the environment's own importing
        // behaviour becomes visible. A script that carries the host PATH answers with it.
        if (hostPath is not null && _answers.TryGetValue(signature + " [with-host-path]", out var imported))
        {
            return Task.FromResult(imported);
        }

        return Task.FromResult(_answers.TryGetValue(signature, out var answer)
            ? answer
            : WslResult.NotStarted);
    }

    /// <summary>Makes the environment import the host PATH the probe hands it.</summary>
    public ScriptedWslEnvironment ImportsTheHostPath()
    {
        _answers[$"-d {EnvironmentId} -u {AgentUser} --exec /usr/bin/printenv PATH [with-host-path]"] =
            new WslResult(
                true,
                0,
                "/usr/local/sbin:/usr/bin:/bin:/mnt/c/corex-hard-isolation-path-probe:/mnt/c/WINDOWS",
                string.Empty);

        return this;
    }
}
