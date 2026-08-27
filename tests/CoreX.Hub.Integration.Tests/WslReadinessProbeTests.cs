using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The probe against a real WSL environment on this machine.
///
/// It is a negative fixture and only a negative fixture. Nothing here creates, imports,
/// configures or modifies anything: no distribution is registered, no account is added, no
/// mount is changed and no file inside any environment is written. The environments it looks at
/// are whatever the machine already has, in whatever state the operator already left them.
///
/// The point is the one thing unit tests cannot show. An ordinary WSL distribution - the kind a
/// developer installs in a minute - is not a boundary: it mounts the host's drives, it can
/// execute host programs, and asking it for root simply works. A design that only ever tested
/// its own fakes would never find that out, and would ship a readiness check that says yes to a
/// machine which offers no isolation at all.
///
/// Every assertion is written to hold on any machine, including one with no WSL at all: an
/// environment that cannot be examined produces Unknowns, and Unknown is never ready.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslReadinessProbeTests
{
    /// <summary>
    /// Asked as root on purpose.
    ///
    /// The alternative - naming the machine's own default account - would make the test depend
    /// on who is running it. Asking as root is the same everywhere and exercises the check that
    /// matters most: an agent identity that is root is contained by nothing, whatever else the
    /// environment gets right.
    /// </summary>
    private const string AgentUser = "root";

    /// <summary>Present on every Debian derived distribution and owns nothing interesting.</summary>
    private const string ServiceUser = "daemon";

    [Fact]
    public async Task An_ordinary_wsl_environment_is_never_ready()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        Assert.False(readiness.IsReady);
        Assert.NotEmpty(readiness.UnmetCheckIds);
    }

    [Fact]
    public async Task Every_declared_check_is_answered_in_the_canonical_order()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        Assert.Equal(HardIsolationChecks.Required, readiness.Checks.Select(c => c.Id));
    }

    /// <summary>
    /// A registered environment can actually be entered.
    ///
    /// <c>root</c> exists in every Linux distribution, so this cannot fail because of how the
    /// machine is set up: if the environment is registered and the probe still cannot ask it who
    /// it is, the probe is broken rather than the machine.
    ///
    /// Worth its own test because that failure is silent in the worst way. Every guest question
    /// would come back Unknown, the environment would correctly be reported as not ready, and the
    /// diagnostic would send an operator looking for a fault in a distribution that is fine.
    /// </summary>
    [Fact]
    public async Task A_registered_environment_can_actually_be_entered()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        if (Status(readiness, HardIsolationChecks.EnvironmentExists) != ReadinessStatus.Pass)
        {
            return;
        }

        Assert.Equal(ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.AgentUserExists));
        Assert.Equal(ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.SystemdAvailable));
    }

    /// <summary>
    /// A root agent is reported as one, and being a normal uid would not have saved it either.
    ///
    /// Only asserted when the environment was actually reachable, because on a machine with no
    /// WSL there is nothing to have an opinion about - and that case is covered by the readiness
    /// test above.
    /// </summary>
    [Fact]
    public async Task A_root_agent_fails_both_privilege_checks()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        if (Status(readiness, HardIsolationChecks.AgentUserExists) != ReadinessStatus.Pass)
        {
            return;
        }

        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotRoot));
        Assert.Equal(ReadinessStatus.Fail, Status(readiness, HardIsolationChecks.AgentUserNotPrivileged));
    }

    /// <summary>
    /// A reachable environment answers every question this build can ask.
    ///
    /// The strongest property this suite can assert about the probe itself: once it is inside a
    /// live environment, nothing is left unknown. An Unknown there would mean a question the
    /// probe cannot actually ask, which would sit in a diagnostic for ever looking like a
    /// machine problem.
    ///
    /// The control plane check is the one deliberate exception and is asserted separately
    /// below - this build has no network probe, and saying so is the honest answer.
    /// </summary>
    [Fact]
    public async Task A_reachable_environment_leaves_nothing_else_unknown()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        if (Status(readiness, HardIsolationChecks.EnvironmentExists) != ReadinessStatus.Pass
            || Status(readiness, HardIsolationChecks.AgentUserExists) != ReadinessStatus.Pass)
        {
            return;
        }

        var unknown = readiness.Checks
            .Where(c => c.Status == ReadinessStatus.Unknown)
            .Select(c => c.Id)
            .Where(id => id != HardIsolationChecks.ControlPlaneUnreachable);

        Assert.Empty(unknown);
    }

    /// <summary>
    /// The control plane check never passes in this build, whatever the machine looks like.
    ///
    /// The question is whether a hard agent can reach the trusted Hub, which has no multi-user
    /// authorisation boundary. An environment with no host volume mounted can still reach it
    /// over loopback forwarding, the host gateway or mirrored networking, and nothing here
    /// probes for that - so a Pass would be a configuration assumption dressed as a
    /// measurement. A filesystem route, when one exists, is still reported as the failure it is.
    /// </summary>
    [Fact]
    public async Task The_control_plane_check_never_passes_without_a_network_probe()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        Assert.NotEqual(
            ReadinessStatus.Pass, Status(readiness, HardIsolationChecks.ControlPlaneUnreachable));
    }

    /// <summary>
    /// The findings agree with each other.
    ///
    /// Host drives and provider credentials are two consequences of one observation, and an
    /// operator who reads them has to be able to trust that they were not decided
    /// independently. If no host volume is mounted at all, the provider credential check may
    /// not claim something is reachable - and if one is, it may not claim otherwise.
    /// </summary>
    [Fact]
    public async Task Reachability_findings_are_consistent_with_the_mount_table()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        var drives = Status(readiness, HardIsolationChecks.HostDrivesNotMounted);

        if (drives == ReadinessStatus.Unknown)
        {
            return;
        }

        Assert.Equal(drives, Status(readiness, HardIsolationChecks.HostProviderAuthUnreachable));
    }

    /// <summary>
    /// Nothing the probe learned inside an environment is a secret.
    ///
    /// Asserted against the machine's own identity rather than a fixed string, so it fails on
    /// the machine where it would matter.
    /// </summary>
    [Fact]
    public async Task Nothing_the_probe_reports_is_worth_stealing()
    {
        var readiness = await ProbeFirstEnvironmentAsync();

        var reported = string.Join(Environment.NewLine, readiness.Checks.Select(c => c.Detail));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.DoesNotContain(profile, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/etc/shadow", reported, StringComparison.Ordinal);
        Assert.DoesNotContain("token", reported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", reported, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An environment nobody registered is a failure, not a pass and not a crash.</summary>
    [Fact]
    public async Task An_environment_that_does_not_exist_fails_rather_than_throws()
    {
        using var temp = new TempDirectory();

        var readiness = await WslReadinessProbe.Create(temp.Path).ProbeAsync(
            "fixture", Profile("corex-environment-that-does-not-exist"), CancellationToken.None);

        Assert.False(readiness.IsReady);
        Assert.Contains(
            Status(readiness, HardIsolationChecks.EnvironmentExists),
            new[] { ReadinessStatus.Fail, ReadinessStatus.Unknown });
    }

    private static ReadinessStatus Status(HardIsolationReadiness readiness, string id) =>
        readiness.Checks.Single(c => c.Id == id).Status;

    /// <summary>
    /// Probes whatever this machine has, or reports that it has nothing.
    ///
    /// A Debian derived distribution is preferred when one is registered, because the service
    /// account the profile names is a Debian convention. Anything else still produces a valid
    /// result - a probe that cannot run <c>id</c> reports Unknown, which is not ready.
    /// </summary>
    private static async Task<HardIsolationReadiness> ProbeFirstEnvironmentAsync()
    {
        using var temp = new TempDirectory();

        var environments = await ListEnvironmentsAsync(temp.Path);

        var chosen = environments.FirstOrDefault(
                         name => name.Contains("ubuntu", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("debian", StringComparison.OrdinalIgnoreCase))
                     ?? environments.FirstOrDefault()
                     ?? "corex-no-environment-registered";

        return await WslReadinessProbe.Create(temp.Path).ProbeAsync(
            "fixture", Profile(chosen), CancellationToken.None);
    }

    /// <summary>
    /// Which environments exist, asked the same way production asks: through ProcessRunner.
    ///
    /// The test deliberately does not reach for Process.Start to find this out. There is one
    /// process boundary in this system and a test that went around it would be proving
    /// something about a path that does not ship.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListEnvironmentsAsync(string workingDirectory)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        if (!File.Exists(executable))
        {
            return [];
        }

        var result = await new ProcessRunner(FileSystemGuard.Create(workingDirectory)).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["--list", "--quiet"],
                WorkingDirectory = workingDirectory,
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromSeconds(60),
            },
            CancellationToken.None);

        return result.ExitCode == 0
            ? [.. result.NormalizedStdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)]
            : [];
    }

    private static HardIsolationProfile Profile(string environmentId)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = environmentId,
            AgentUser = AgentUser,
            ServiceUser = ServiceUser,

            // Never created, by design. A readiness probe that made the directories it was
            // asked about would report on its own work rather than on the machine.
            ExecutionRoot = "/srv/corex-readiness-fixture/exec",
            EvidenceRoot = "/srv/corex-readiness-fixture/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "corex-wsl-probe", Guid.CreateVersion7().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A locked temp directory must never fail a test run.
            }
        }
    }
}
