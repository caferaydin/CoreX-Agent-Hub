using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The production mount boundary, against two real disposable environments.
///
/// Every test here does nothing unless an operator has explicitly named two disposable
/// environments in the process environment, and that is the whole safety model: this class starts
/// transient units, writes markers onto a surface shared by every distribution on the machine, and
/// runs commands as a synthetic agent account, so it must never run because somebody typed
/// <c>dotnet test</c>. It creates and destroys nothing - provisioning is the operator's step, done
/// deliberately - and it uses only what it was pointed at.
///
/// What it proves is the part no scripted test can. The scripted tests establish that the rendered
/// systemd vector is the one the policy asks for; these establish that the vector, given to a real
/// systemd, actually denies a real file to a real account - and, because there are two
/// environments, that the file it denies is one the other organization really could otherwise
/// read.
///
/// Both fixtures are expected to carry: a <c>cxagent</c> account, a <c>cxpeer</c> account with an
/// unreadable synthetic auth marker, a <c>cxservice</c> owned evidence root at
/// <c>/srv/corex/evidence</c>, and an agent owned workspace under <c>/srv/corex/exec</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardFilesystemBoundaryAcceptanceTests
{
    /// <summary>The surface measured to be one kernel device in every distribution.</summary>
    private const string SharedRoot = "/mnt/wsl";

    private const string MarkerA = SharedRoot + "/corex-acceptance-marker-a.txt";
    private const string MarkerB = SharedRoot + "/corex-acceptance-marker-b.txt";

    /// <summary>Synthetic content, so a crossing is a crossing of something and not of a name.</summary>
    private const string ContentA = "corex-synthetic-payload-from-a";

    private const string ContentB = "corex-synthetic-payload-from-b";

    private const string Agent = "cxagent";
    private const string Root = "root";

    /// <summary>The two disposable environments, or nothing, in which case every test is inert.</summary>
    private static (string A, string B)? Environments()
    {
        var a = Environment.GetEnvironmentVariable("COREX_FILESYSTEM_A");
        var b = Environment.GetEnvironmentVariable("COREX_FILESYSTEM_B");

        return string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ? null : (a!, b!);
    }

    private static HardIsolationProfile Profile(string environment)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = environment,
            AgentUser = Agent,
            ServiceUser = Root,
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    /// <summary>Runs one command in an environment, for arranging and for reading back.</summary>
    private static Task<(int? ExitCode, string Stdout)> RunAsync(
        string environment, string user, params string[] arguments) =>
        RunWithInputAsync(environment, user, null, arguments);

    private static async Task<(int? ExitCode, string Stdout)> RunWithInputAsync(
        string environment, string user, string? stdin, params string[] arguments)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wsl.exe");

        var result = await new ProcessRunner(FileSystemGuard.Create(Path.GetTempPath())).RunAsync(
            new ProcessRequest
            {
                Executable = executable,
                Arguments = ["-d", environment, "-u", user, "--exec", .. arguments],
                WorkingDirectory = Path.GetTempPath(),
                EnvironmentAllowlist = ["SystemRoot"],
                AdditionalEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WSL_UTF8"] = "1",
                },
                HardTimeout = TimeSpan.FromMinutes(2),
                MaxCapturedCharactersPerStream = 1024 * 1024,
                StdinPayload = stdin,
            },
            CancellationToken.None);

        return (result.ExitCode, result.NormalizedStdout);
    }

    /// <summary>
    /// Runs one command the way a hard workload runs: as the agent, inside the real boundary.
    ///
    /// The properties come from <see cref="HardMountSandbox"/> and not from a list here. A test
    /// that spelled its own sandbox would pass while the shipped one did nothing.
    /// </summary>
    private static Task<(int? ExitCode, string Stdout)> AsWorkloadAsync(
        string environment, params string[] command) =>
        RunAsync(
            environment,
            Root,
            [
                SystemdRunCommand.Executable, "--quiet", "--pipe", "--wait", "--collect",
                "--uid=" + Agent,
                .. HardMountSandbox.Properties(HardFilesystemPolicy.Phase10).Select(p => "--property=" + p),
                "--",
                .. command,
            ]);

    /// <summary>The same command as the agent, with no boundary at all. The control.</summary>
    private static Task<(int? ExitCode, string Stdout)> AsBareAgentAsync(
        string environment, params string[] command) =>
        RunAsync(environment, Agent, command);

    /// <summary>Puts each organization's marker on the shared surface, from the trusted identity.</summary>
    private static async Task ArrangeMarkersAsync(string a, string b)
    {
        await RunWithInputAsync(a, Root, ContentA, "/usr/bin/tee", MarkerA);
        await RunAsync(a, Root, "/usr/bin/chmod", "0666", MarkerA);
        await RunWithInputAsync(b, Root, ContentB, "/usr/bin/tee", MarkerB);
        await RunAsync(b, Root, "/usr/bin/chmod", "0666", MarkerB);
    }

    // -- the surface is real ---------------------------------------------------------------------

    /// <summary>
    /// The shared surface really does carry a file from one environment to the other.
    ///
    /// First, because every denial below is worthless without it. A boundary that hides a
    /// directory nothing was ever shared through would pass every other test here and close
    /// nothing, and this is the test that would still fail if WSL stopped sharing the mount.
    /// </summary>
    [Fact]
    public async Task The_shared_surface_carries_a_file_between_the_two_environments()
    {
        if (Environments() is not var (a, b))
        {
            return;
        }

        await ArrangeMarkersAsync(a, b);

        // Written in one environment, read in the other, from the trusted identity - which is the
        // identity that will still see the surface after the boundary is in place.
        var aReadsB = await RunAsync(a, Root, "/usr/bin/cat", MarkerB);
        var bReadsA = await RunAsync(b, Root, "/usr/bin/cat", MarkerA);

        Assert.Equal(ContentB, aReadsB.Stdout.Trim());
        Assert.Equal(ContentA, bReadsA.Stdout.Trim());

        // And the same kernel device underneath, not two directories that happen to agree.
        var deviceA = await RunAsync(a, Root, "/usr/bin/stat", "-c", "%d", SharedRoot);
        var deviceB = await RunAsync(b, Root, "/usr/bin/stat", "-c", "%d", SharedRoot);

        Assert.Equal(deviceA.Stdout.Trim(), deviceB.Stdout.Trim());
    }

    /// <summary>
    /// Without the boundary the agent account can use it, in both directions.
    ///
    /// The defect this phase closes, stated as a passing test so that a future change which
    /// removes the boundary fails something other than a comment.
    /// </summary>
    [Fact]
    public async Task Without_the_boundary_the_agent_reaches_the_other_organizations_marker()
    {
        if (Environments() is not var (a, b))
        {
            return;
        }

        await ArrangeMarkersAsync(a, b);

        Assert.Equal(ContentB, (await AsBareAgentAsync(a, "/usr/bin/cat", MarkerB)).Stdout.Trim());
        Assert.Equal(ContentA, (await AsBareAgentAsync(b, "/usr/bin/cat", MarkerA)).Stdout.Trim());

        // And writing, which is what makes it a channel rather than a leak.
        Assert.Equal(
            0,
            (await AsBareAgentAsync(a, "/usr/bin/touch", SharedRoot + "/corex-bare-agent-write.txt")).ExitCode);
    }

    // -- the boundary ----------------------------------------------------------------------------

    /// <summary>
    /// Inside the boundary, neither organization's workload can reach the other's marker.
    ///
    /// The peer claim, measured rather than derived: two environments, a real file each, and the
    /// agent account in both refused.
    /// </summary>
    [Fact]
    public async Task Neither_workload_can_reach_the_other_organizations_marker()
    {
        if (Environments() is not var (a, b))
        {
            return;
        }

        await ArrangeMarkersAsync(a, b);

        foreach (var (environment, peerMarker, ownMarker) in
                 new[] { (a, MarkerB, MarkerA), (b, MarkerA, MarkerB) })
        {
            // Not the peer's, and not its own either: the surface is closed, not filtered.
            Assert.NotEqual(0, (await AsWorkloadAsync(environment, "/usr/bin/cat", peerMarker)).ExitCode);
            Assert.NotEqual(0, (await AsWorkloadAsync(environment, "/usr/bin/cat", ownMarker)).ExitCode);
            Assert.NotEqual(0, (await AsWorkloadAsync(environment, "/usr/bin/ls", "-A", SharedRoot)).ExitCode);
        }
    }

    /// <summary>
    /// Every denied surface is refused for listing, reading and writing.
    ///
    /// Driven from the policy rather than from a list here, so a surface added to the policy and
    /// not to the boundary fails without anybody remembering to extend this test.
    /// </summary>
    [Fact]
    public async Task Every_denied_surface_is_refused_to_a_workload()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        foreach (var surface in HardFilesystemPolicy.Phase10.DeniedSurfaces)
        {
            Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/ls", "-A", surface.Path)).ExitCode);
            Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/test", "-r", surface.Path)).ExitCode);

            Assert.NotEqual(
                0,
                (await AsWorkloadAsync(a, "/usr/bin/touch", surface.Path + "/corex-write-probe.txt")).ExitCode);
        }
    }

    /// <summary>
    /// The denial survives every way of spelling the path that was measured to work without it.
    ///
    /// A path check would fail all of these; an overmounted directory fails none of them, which is
    /// why the boundary is a mount and not a rule about strings.
    /// </summary>
    [Fact]
    public async Task The_denial_survives_traversal_symlinks_and_proc()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        await RunWithInputAsync(a, Root, ContentA, "/usr/bin/tee", MarkerA);
        await RunAsync(a, Root, "/usr/bin/chmod", "0666", MarkerA);

        foreach (var spelling in new[]
                 {
                     "/mnt/wsl/../wsl/corex-acceptance-marker-a.txt",
                     "/tmp/../mnt/wsl/corex-acceptance-marker-a.txt",
                     "/proc/self/root/mnt/wsl/corex-acceptance-marker-a.txt",
                     "/proc/1/root/mnt/wsl/corex-acceptance-marker-a.txt",
                 })
        {
            Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/cat", spelling)).ExitCode);
        }

        // A symlink the agent owns, pointing at the surface from inside its own workspace.
        var link = "/srv/corex/exec/workspace/corex-link-to-shared";

        await AsBareAgentAsync(a, "/usr/bin/ln", "-sfn", SharedRoot, link);

        Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/ls", "-A", link)).ExitCode);
        Assert.NotEqual(
            0,
            (await AsWorkloadAsync(a, "/usr/bin/cat", link + "/corex-acceptance-marker-a.txt")).ExitCode);
    }

    /// <summary>
    /// A child user namespace inherits the boundary and cannot get back out of it.
    ///
    /// The agent can still create one - nothing here claims user namespaces are disabled, and
    /// claiming it would be false. What is established is narrower and is the thing that matters:
    /// the child starts from the masked view, and the parent's mount namespace is not reachable
    /// from inside it.
    /// </summary>
    [Fact]
    public async Task A_child_namespace_inherits_the_boundary()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        await RunWithInputAsync(a, Root, ContentA, "/usr/bin/tee", MarkerA);
        await RunAsync(a, Root, "/usr/bin/chmod", "0666", MarkerA);

        // It really can make one. Stated as a passing expectation so the tests below cannot be
        // satisfied by the escape simply being unavailable.
        Assert.Equal(
            0,
            (await AsWorkloadAsync(
                a, "/usr/bin/unshare", "--user", "--mount", "--map-root-user", "/usr/bin/true")).ExitCode);

        Assert.NotEqual(
            0,
            (await AsWorkloadAsync(
                a, "/usr/bin/unshare", "--user", "--mount", "--map-root-user",
                "/usr/bin/cat", MarkerA)).ExitCode);

        // And the parent mount namespace is not reachable, by either spelling.
        Assert.NotEqual(
            0, (await AsWorkloadAsync(a, "/usr/bin/nsenter", "-t", "1", "-m", "/usr/bin/ls", SharedRoot)).ExitCode);

        Assert.NotEqual(
            0,
            (await AsWorkloadAsync(
                a, "/usr/bin/nsenter", "--mount=/proc/1/ns/mnt", "/usr/bin/ls", SharedRoot)).ExitCode);
    }

    /// <summary>
    /// The workload holds no capabilities, and cannot acquire any.
    ///
    /// <c>CAP_SYS_ADMIN</c> in the namespace that owns the boundary would be the boundary's own
    /// undoing, so the effective set is empty and the bounding set is too.
    /// </summary>
    [Fact]
    public async Task The_workload_holds_no_capabilities()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        var status = await AsWorkloadAsync(a, "/usr/bin/cat", "/proc/self/status");

        Assert.Equal(0, status.ExitCode);

        foreach (var set in new[] { "CapEff", "CapPrm", "CapBnd" })
        {
            var line = status.Stdout
                .Split('\n')
                .Single(l => l.StartsWith(set + ":", StringComparison.Ordinal));

            Assert.Equal("0000000000000000", line.Split(':')[1].Trim());
        }
    }

    // -- the boundary does not deny the work -----------------------------------------------------

    /// <summary>
    /// Inside the boundary the workload can still do everything it is supposed to.
    ///
    /// Without this the whole phase is satisfiable by denying the filesystem, which would pass
    /// every negative test above.
    /// </summary>
    [Fact]
    public async Task The_workload_can_still_use_its_workspace_home_and_temp()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        var tracked = "/srv/corex/exec/workspace/tracked.txt";

        Assert.Equal(0, (await AsWorkloadAsync(a, "/usr/bin/cat", tracked)).ExitCode);
        Assert.Equal(0, (await AsWorkloadAsync(a, "/usr/bin/test", "-w", tracked)).ExitCode);

        Assert.Equal(
            0,
            (await AsWorkloadAsync(a, "/usr/bin/touch", "/srv/corex/exec/workspace/new-source.txt")).ExitCode);

        // Its own provider auth state, in its own Linux native home.
        Assert.Equal(0, (await AsWorkloadAsync(a, "/usr/bin/cat", "/home/cxagent/.corex-auth")).ExitCode);
        Assert.Equal(0, (await AsWorkloadAsync(a, "/usr/bin/test", "-w", "/home/cxagent/.corex-auth")).ExitCode);

        // And somewhere to write scratch files, which the private tmpfs provides.
        Assert.Equal(0, (await AsWorkloadAsync(a, "/usr/bin/touch", "/tmp/corex-scratch.txt")).ExitCode);
    }

    /// <summary>
    /// The boundary does not weaken any denial that was already in place.
    ///
    /// A peer account's auth marker and the service owned evidence root were unreachable before
    /// this phase, and a mount namespace that accidentally re-exposed either would be a
    /// regression the negative tests above would not notice.
    /// </summary>
    [Fact]
    public async Task The_boundary_keeps_the_denials_that_were_already_there()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/cat", "/home/cxpeer/.corex-auth")).ExitCode);
        Assert.NotEqual(0, (await AsWorkloadAsync(a, "/usr/bin/ls", "-A", "/srv/corex/evidence")).ExitCode);

        Assert.NotEqual(
            0,
            (await AsWorkloadAsync(a, "/usr/bin/cat", "/srv/corex/evidence/blobs/synthetic.txt")).ExitCode);

        Assert.NotEqual(
            0, (await AsWorkloadAsync(a, "/usr/bin/touch", "/srv/corex/evidence/agent.txt")).ExitCode);
    }

    /// <summary>
    /// No Windows volume is reachable, measured rather than assumed from configuration.
    ///
    /// Automount is expected to be off in a hard environment, but "expected" is what this whole
    /// phase exists to stop relying on.
    /// </summary>
    [Fact]
    public async Task No_windows_volume_is_reachable_from_a_workload()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        var mounts = await AsWorkloadAsync(a, "/usr/bin/cat", "/proc/self/mountinfo");

        Assert.Equal(0, mounts.ExitCode);

        var driveMounts = mounts.Stdout
            .Split('\n')
            .Where(l => l.Contains(" drvfs ", StringComparison.Ordinal)
                        || l.Contains(" 9p ", StringComparison.Ordinal)
                        || l.Contains(" v9fs ", StringComparison.Ordinal))
            .Where(l => l.Contains(" /mnt/", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(driveMounts);

        foreach (var drive in new[] { "/mnt/c", "/mnt/d" })
        {
            var listed = await AsWorkloadAsync(a, "/usr/bin/ls", "-A", drive);

            // Either it is not there, or it is an empty directory that carries no volume.
            Assert.True(
                listed.ExitCode != 0 || listed.Stdout.Trim().Length == 0,
                drive + " carries: " + listed.Stdout);
        }
    }

    // -- the boundary and the network boundary together ------------------------------------------

    /// <summary>
    /// The mount boundary and the explicit network namespace hold at the same time.
    ///
    /// Solving one boundary by dropping the other would be a straightforward way to make both
    /// suites pass separately, so the workload is launched with the frozen
    /// <c>NetworkNamespacePath</c> model and its own kernel network identity is compared against
    /// the holder's while the filesystem denials are re-checked in that same run.
    /// </summary>
    [Fact]
    public async Task The_mount_boundary_holds_inside_an_explicit_network_namespace()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        const string Holder = "corex-fs-acceptance-holder.service";

        await RunAsync(a, Root, "/usr/bin/systemctl", "stop", Holder);

        var started = await RunAsync(
            a, Root,
            SystemdRunCommand.Executable, "--quiet", "--unit=" + Holder,
            "--property=PrivateNetwork=yes", "--property=Type=exec", "--property=KillMode=control-group",
            "--", "/usr/bin/sleep", "300");

        Assert.Equal(0, started.ExitCode);

        try
        {
            var mainPid = (await RunAsync(
                a, Root, "/usr/bin/systemctl", "show", Holder, "--property=MainPID", "--value")).Stdout.Trim();

            Assert.NotEqual("0", mainPid);

            var reference = "/proc/" + mainPid + "/ns/net";

            var expected = (await RunAsync(a, Root, "/usr/bin/readlink", reference)).Stdout.Trim();
            var host = (await RunAsync(a, Root, "/usr/bin/readlink", "/proc/1/ns/net")).Stdout.Trim();

            Assert.NotEqual(host, expected);

            async Task<(int? ExitCode, string Stdout)> BothAsync(params string[] command) =>
                await RunAsync(
                    a, Root,
                    [
                        SystemdRunCommand.Executable, "--quiet", "--pipe", "--wait", "--collect",
                        "--uid=" + Agent,
                        "--property=NetworkNamespacePath=" + reference,
                        .. HardMountSandbox.Properties(HardFilesystemPolicy.Phase10)
                            .Select(p => "--property=" + p),
                        "--",
                        .. command,
                    ]);

            // The network identity is the organization's, not the environment's.
            var landed = await BothAsync("/usr/bin/readlink", "/proc/self/ns/net");

            Assert.Equal(0, landed.ExitCode);
            Assert.Equal(expected, landed.Stdout.Trim());

            // And the filesystem denials hold in that same run.
            Assert.NotEqual(0, (await BothAsync("/usr/bin/ls", "-A", SharedRoot)).ExitCode);
            Assert.NotEqual(0, (await BothAsync("/usr/bin/ls", "-A", "/mnt/wslg")).ExitCode);

            // While the work still works.
            Assert.Equal(
                0, (await BothAsync("/usr/bin/test", "-w", "/srv/corex/exec/workspace")).ExitCode);
        }
        finally
        {
            await RunAsync(a, Root, "/usr/bin/systemctl", "stop", Holder);
        }
    }

    // -- fail closed -----------------------------------------------------------------------------

    /// <summary>
    /// A boundary that cannot be established is a run that never starts.
    ///
    /// The marker is the evidence, exactly as it is for the network race: if the workload had
    /// started and the sandbox had been applied afterwards - or not at all - the file would exist.
    /// Each malformed property is a different failure inside systemd, and none of them may become
    /// an execution.
    /// </summary>
    [Theory]
    [InlineData("InaccessiblePaths=not-absolute")]
    [InlineData("PrivateMounts=maybe")]
    [InlineData("NetworkNamespacePath=/proc/999999/ns/net")]
    [InlineData("RootDirectory=/no/such/root")]
    public async Task A_boundary_that_cannot_be_established_never_execs(string broken)
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        var marker = "/srv/corex/exec/workspace/corex-failclosed-"
                     + broken.Split('=')[0].ToLowerInvariant() + ".txt";

        await RunAsync(a, Root, "/usr/bin/rm", "-f", marker);

        var attempt = await RunAsync(
            a, Root,
            [
                SystemdRunCommand.Executable, "--quiet", "--pipe", "--wait", "--collect",
                "--uid=" + Agent,
                .. HardMountSandbox.Properties(HardFilesystemPolicy.Phase10).Select(p => "--property=" + p),
                "--property=" + broken,
                "--",
                "/usr/bin/touch", marker,
            ]);

        Assert.NotEqual(0, attempt.ExitCode);

        // The workload never ran, which is the only acceptable answer.
        Assert.NotEqual(0, (await RunAsync(a, Root, "/usr/bin/test", "-e", marker)).ExitCode);
    }

    // -- readiness -------------------------------------------------------------------------------

    /// <summary>
    /// Readiness answers the filesystem questions from the workload, and gets them right.
    ///
    /// The production probe rather than a reimplementation of it. Before this phase the peer
    /// filesystem check read a mount table and therefore failed permanently in every environment;
    /// against a real machine it now passes, and it passes because a workload was started and
    /// refused rather than because a file said so.
    /// </summary>
    [Fact]
    public async Task Readiness_establishes_the_boundary_from_the_workload()
    {
        if (Environments() is not var (a, _))
        {
            return;
        }

        var readiness = await WslReadinessProbe.Create(Path.GetTempPath())
            .ProbeAsync("corex-filesystem-org-a", Profile(a), CancellationToken.None);

        foreach (var id in new[]
                 {
                     HardIsolationChecks.SharedFilesystemUnreachable,
                     HardIsolationChecks.OtherHardEnvironmentUnreachable,
                     HardIsolationChecks.WorkloadFilesystemUsable,
                 })
        {
            var check = readiness.Checks.Single(c => c.Id == id);

            Assert.Equal(ReadinessStatus.Pass, check.Status);
        }
    }
}
