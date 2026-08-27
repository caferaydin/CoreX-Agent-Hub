using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The command that asks for containment, proven without a machine.
///
/// Every option in this vector is a security decision. A missing <c>TasksMax</c>, a
/// <c>KillMode</c> that is not <c>control-group</c>, a working directory outside the execution
/// root, a unit name that came from somewhere other than the Hub - each produces a run that
/// looks entirely normal and is not contained, which is exactly why the vector is built by a
/// pure function and compared against a literal here.
/// </summary>
public sealed class HardProcessCommandTests
{
    private const string Unit = "corex-hub-0123456789abcdef0123456789abcdef.service";

    private static readonly Guid Correlation = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    private static HardIsolationProfile Profile(Action<HardIsolationConfig>? adjust = null)
    {
        var config = new HardIsolationConfig
        {
            Backend = "Wsl2",
            EnvironmentId = "corex-fixture",
            AgentUser = "corex-agent",
            ServiceUser = "corex-hub",
            ExecutionRoot = "/srv/corex/exec",
            EvidenceRoot = "/srv/corex/evidence",
            ProcessLimit = 64,
            MemoryLimitBytes = 4L * 1024 * 1024 * 1024,
        };

        adjust?.Invoke(config);

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    private static HardProcessRequest Request(Action<Builder>? adjust = null)
    {
        var builder = new Builder();
        adjust?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>A mutable shape for the record, so each test changes one thing.</summary>
    private sealed class Builder
    {
        public string Executable { get; set; } = "/usr/bin/id";
        public IReadOnlyList<string> Arguments { get; set; } = ["-u"];
        public string WorkingDirectory { get; set; } = "/srv/corex/exec/run-1";
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
        public TimeSpan HardTimeout { get; set; } = TimeSpan.FromMinutes(10);
        public Guid CorrelationId { get; set; } = Correlation;

        public HardProcessRequest Build() => new()
        {
            Executable = Executable,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            Environment = Environment,
            HardTimeout = HardTimeout,
            CorrelationId = CorrelationId,
        };
    }

    // -- the whole vector -------------------------------------------------------------------

    /// <summary>
    /// The exact command, compared against a literal.
    ///
    /// Written out in full rather than asserted piece by piece, because the thing worth catching
    /// is an option that quietly stops being emitted - and a test that only checks the options it
    /// remembers to look for would not notice.
    /// </summary>
    [Fact]
    public void The_invocation_is_exactly_this()
    {
        Assert.True(SystemdRunCommand.TryBuild(Profile(), Request(), Unit, out var argv, out var problem), problem);

        Assert.Equal(
            [
                "/usr/bin/systemd-run",
                "--quiet",
                "--wait",
                "--pipe",
                "--collect",
                "--unit=" + Unit,
                "--uid=corex-agent",
                "--working-directory=/srv/corex/exec/run-1",
                "--property=TasksMax=64",
                "--property=MemoryMax=4294967296",
                "--property=MemorySwapMax=0",
                "--property=KillMode=control-group",
                "--property=RuntimeMaxSec=615",

                // The shared WSL filesystem surfaces, closed before the workload starts.
                "--property=PrivateMounts=yes",
                "--property=InaccessiblePaths=/mnt/wsl",
                "--property=InaccessiblePaths=/mnt/wslg",
                "--property=InaccessiblePaths=/run/user",
                "--property=InaccessiblePaths=-/tmp/.X11-unix",
                "--property=ProtectProc=invisible",
                "--property=CapabilityBoundingSet=",
                "--property=NoNewPrivileges=yes",

                "--",
                "/usr/bin/id",
                "-u",
            ],
            argv);
    }

    /// <summary>
    /// There is no shell in it, and no command string for one to interpret.
    ///
    /// The executable and every argument stay separate vector entries, so a workload argument
    /// containing a space, a quote or a semicolon is an argument and can never become syntax.
    /// </summary>
    [Fact]
    public void Nothing_in_the_invocation_is_a_shell()
    {
        var request = Request(b =>
        {
            b.Executable = "/usr/bin/printf";
            b.Arguments = ["%s\n", "a b; rm -rf /", "$(whoami)", "`id`", "\"quoted\""];
        });

        Assert.True(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out _));

        Assert.DoesNotContain(argv, a => a is "/bin/sh" or "/bin/bash" or "-c" or "sh" or "bash");

        // Every workload argument survives as its own entry, unaltered.
        Assert.Equal(
            ["/usr/bin/printf", "%s\n", "a b; rm -rf /", "$(whoami)", "`id`", "\"quoted\""],
            argv.Skip(argv.ToList().IndexOf("--") + 1));
    }

    /// <summary>
    /// Everything after the separator is the workload, so the workload cannot add an option.
    ///
    /// Without it an argument that happens to begin with two dashes would be read by systemd-run
    /// as one of its own - which is a way to ask for a different user, or no ceilings at all.
    /// </summary>
    [Fact]
    public void A_workload_argument_that_looks_like_an_option_stays_an_argument()
    {
        var request = Request(b =>
        {
            b.Executable = "/usr/bin/id";
            b.Arguments = ["--uid=root", "--property=TasksMax=99999"];
        });

        Assert.True(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out _));

        var separator = argv.ToList().IndexOf("--");

        Assert.Equal("--uid=corex-agent", argv.Take(separator).Single(a => a.StartsWith("--uid=", StringComparison.Ordinal)));
        Assert.Single(argv.Take(separator), a => a.StartsWith("--property=TasksMax=", StringComparison.Ordinal));
        Assert.Equal(["/usr/bin/id", "--uid=root", "--property=TasksMax=99999"], argv.Skip(separator + 1));
    }

    // -- ceilings ---------------------------------------------------------------------------

    [Fact]
    public void The_process_ceiling_comes_from_the_profile_unchanged()
    {
        var profile = Profile(c => c.ProcessLimit = 7);

        Assert.True(SystemdRunCommand.TryBuild(profile, Request(), Unit, out var argv, out _));
        Assert.Contains("--property=TasksMax=7", argv);
    }

    /// <summary>
    /// The memory ceiling is stated, and so is the swap allowance that makes it one.
    ///
    /// Measured, not assumed: under a 64 MiB MemoryMax with no swap property, a workload asked
    /// for 192 MiB got all 192 MiB. Everything above the ceiling had simply gone to swap, and a
    /// limit that can be exceeded by paging is not a limit on what the run consumes.
    /// </summary>
    [Fact]
    public void The_memory_ceiling_comes_from_the_profile_and_allows_no_swap()
    {
        var profile = Profile(c => c.MemoryLimitBytes = 268435456);

        Assert.True(SystemdRunCommand.TryBuild(profile, Request(), Unit, out var argv, out _));
        Assert.Contains("--property=MemoryMax=268435456", argv);
        Assert.Contains("--property=MemorySwapMax=0", argv);
    }

    /// <summary>
    /// Ending the control group, not the process the Hub happens to know about.
    ///
    /// The other kill modes end the main process and leave its descendants running, which for an
    /// agent that spawns a toolchain is the difference between stopping a run and appearing to.
    /// </summary>
    [Fact]
    public void The_kill_mode_is_the_whole_control_group()
    {
        Assert.True(SystemdRunCommand.TryBuild(Profile(), Request(), Unit, out var argv, out _));
        Assert.Contains("--property=KillMode=control-group", argv);
    }

    /// <summary>
    /// The environment's ceiling is longer than the caller's, on purpose.
    ///
    /// Shorter would mean the environment ending work the caller was still entitled to; equal
    /// would make the race between the two deadlines decide which one gets reported. Longer means
    /// the host wins whenever it is alive, and the environment still bounds the work when it is
    /// not.
    /// </summary>
    [Theory]
    [InlineData(60, 75)]
    [InlineData(600, 615)]
    [InlineData(1, 16)]
    public void The_environment_runtime_ceiling_outlasts_the_host_deadline(int hardSeconds, long expected)
    {
        var request = Request(b => b.HardTimeout = TimeSpan.FromSeconds(hardSeconds));

        Assert.Equal(expected, SystemdRunCommand.RuntimeSeconds(request.HardTimeout));
        Assert.True(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out _));
        Assert.Contains($"--property=RuntimeMaxSec={expected}", argv);
    }

    /// <summary>A fractional deadline rounds up; rounding down would shorten it.</summary>
    [Fact]
    public void A_fractional_deadline_rounds_upwards()
    {
        Assert.Equal(
            15 + 61, SystemdRunCommand.RuntimeSeconds(TimeSpan.FromSeconds(60.2)));
    }

    // -- fail closed ------------------------------------------------------------------------

    /// <summary>
    /// Phase 10C-1 already refuses a profile with no finite ceiling. This is the second lock.
    ///
    /// The primitive is callable directly, and a caller that reached it with a zero limit must
    /// not get an uncontained run out of it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_profile_without_a_finite_process_ceiling_is_refused(int limit)
    {
        var profile = Profile() with { ProcessLimit = limit };

        Assert.False(SystemdRunCommand.TryBuild(profile, Request(), Unit, out var argv, out var problem));
        Assert.Empty(argv);
        Assert.Contains("TasksMax", problem);
    }

    [Fact]
    public void A_profile_without_a_finite_memory_ceiling_is_refused()
    {
        var profile = Profile() with { MemoryLimitBytes = 0 };

        Assert.False(SystemdRunCommand.TryBuild(profile, Request(), Unit, out _, out var problem));
        Assert.Contains("MemoryMax", problem);
    }

    [Fact]
    public void A_request_without_a_hard_runtime_is_refused()
    {
        var request = Request(b => b.HardTimeout = TimeSpan.Zero);

        Assert.False(SystemdRunCommand.TryBuild(Profile(), request, Unit, out _, out var problem));
        Assert.Contains("RuntimeMaxSec", problem);
    }

    /// <summary>A profile that names no agent user is refused, never defaulted to root.</summary>
    [Fact]
    public void A_profile_without_an_agent_user_is_refused()
    {
        var profile = Profile() with { AgentUser = "   " };

        Assert.False(SystemdRunCommand.TryBuild(profile, Request(), Unit, out var argv, out var problem));
        Assert.Empty(argv);
        Assert.Contains("never runs as root", problem);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("../usr/bin/id")]
    [InlineData("")]
    public void A_workload_executable_that_is_not_an_absolute_path_is_refused(string executable)
    {
        var request = Request(b => b.Executable = executable);

        Assert.False(SystemdRunCommand.TryBuild(Profile(), request, Unit, out _, out var problem));
        Assert.Contains("absolute path", problem);
    }

    // -- working directory ------------------------------------------------------------------

    [Theory]
    [InlineData("/srv/corex/exec")]
    [InlineData("/srv/corex/exec/run-1")]
    [InlineData("/srv/corex/exec/run-1/nested")]
    public void A_working_directory_inside_the_execution_root_is_accepted(string directory)
    {
        var request = Request(b => b.WorkingDirectory = directory);

        Assert.True(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out var problem), problem);
        Assert.Contains("--working-directory=" + directory, argv);
    }

    /// <summary>
    /// Containment is decided on segments, so a path that reads as if it were inside is not.
    ///
    /// <c>/srv/corex/execution</c> is the case a prefix comparison gets wrong, and
    /// <c>/srv/corex/exec/../../etc</c> is the case a textual one gets wrong.
    /// </summary>
    [Theory]
    [InlineData("/srv/corex/execution")]
    [InlineData("/srv/corex/exec/../../etc")]
    [InlineData("/srv/corex")]
    [InlineData("/etc")]
    [InlineData("/mnt/c/Users")]
    [InlineData("/proc/self")]
    [InlineData(@"C:\corex\exec")]
    [InlineData("srv/corex/exec")]
    [InlineData("/")]
    public void A_working_directory_outside_the_execution_root_is_refused(string directory)
    {
        var request = Request(b => b.WorkingDirectory = directory);

        Assert.False(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out var problem));
        Assert.Empty(argv);
        Assert.Contains("working directory", problem);
    }

    // -- environment ------------------------------------------------------------------------

    /// <summary>
    /// The workload's environment is exactly what was asked for, in a stable order.
    ///
    /// Nothing of the Windows caller appears, because there is no mechanism by which it could:
    /// the request carries values rather than names to inherit.
    /// </summary>
    [Fact]
    public void The_environment_is_exactly_what_the_request_named()
    {
        var request = Request(b => b.Environment = new(StringComparer.Ordinal)
        {
            ["COREX_B"] = "second",
            ["COREX_A"] = "first",
        });

        Assert.True(SystemdRunCommand.TryBuild(Profile(), request, Unit, out var argv, out _));

        Assert.Equal(
            ["--setenv=COREX_A=first", "--setenv=COREX_B=second"],
            argv.Where(a => a.StartsWith("--setenv=", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_request_with_no_environment_forwards_nothing()
    {
        Assert.True(SystemdRunCommand.TryBuild(Profile(), Request(), Unit, out var argv, out _));
        Assert.DoesNotContain(argv, a => a.StartsWith("--setenv=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HAS=EQUALS")]
    public void An_unusable_environment_name_is_refused(string name)
    {
        var request = Request(b => b.Environment = new(StringComparer.Ordinal) { [name] = "value" });

        Assert.False(SystemdRunCommand.TryBuild(Profile(), request, Unit, out _, out var problem));
        Assert.Contains("environment variable name", problem);
    }

    // -- unit identity ----------------------------------------------------------------------

    [Fact]
    public void A_unit_name_is_derived_from_hub_identity_alone()
    {
        Assert.Equal(Unit, HardUnitName.For(Correlation));
        Assert.True(HardUnitName.IsHubOwned(Unit));
    }

    [Fact]
    public void Two_runs_never_share_a_unit_name()
    {
        Assert.NotEqual(HardUnitName.For(Guid.CreateVersion7()), HardUnitName.For(Guid.CreateVersion7()));
    }

    [Fact]
    public void A_run_without_an_identity_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HardUnitName.For(Guid.Empty));
    }

    /// <summary>
    /// Only a name this Hub produced is recognised as its own.
    ///
    /// The list below is what a unit name would look like if it had come from a task objective, a
    /// repository, an API parameter or an agent's output - and every one of them is refused, which
    /// is what stops the cancellation path being a way to stop anything on the machine.
    /// </summary>
    [Theory]
    [InlineData("ssh.service")]
    [InlineData("corex-hub-.service")]
    [InlineData("corex-hub-not-hex.service")]
    [InlineData("corex-hub-0123456789ABCDEF0123456789ABCDEF.service")]
    [InlineData("corex-hub-0123456789abcdef0123456789abcdef")]
    [InlineData("corex-hub-0123456789abcdef0123456789abcdef.service.evil")]
    [InlineData("prefix-corex-hub-0123456789abcdef0123456789abcdef.service")]
    [InlineData("")]
    [InlineData(null)]
    public void A_unit_name_from_anywhere_else_is_not_hub_owned(string? candidate)
    {
        Assert.False(HardUnitName.IsHubOwned(candidate));
    }

    [Fact]
    public void An_invocation_with_a_unit_name_the_hub_did_not_create_is_refused()
    {
        Assert.False(SystemdRunCommand.TryBuild(Profile(), Request(), "ssh.service", out var argv, out var problem));
        Assert.Empty(argv);
        Assert.Contains("not a unit name this Hub created", problem);
    }

    // -- cancellation authority -------------------------------------------------------------

    [Fact]
    public void The_stop_command_names_the_unit_and_nothing_else()
    {
        Assert.True(SystemdRunCommand.TryBuildStop(Unit, out var argv, out _));
        Assert.Equal(["/usr/bin/systemctl", "stop", Unit], argv);
    }

    /// <summary>
    /// Cancellation can only ever reach a unit this Hub created.
    ///
    /// The interesting entries are the ones that are perfectly valid systemd unit names - a
    /// cancellation path that accepted them would be a general purpose way to stop services on
    /// the operator's machine.
    /// </summary>
    [Theory]
    [InlineData("ssh.service")]
    [InlineData("systemd-journald.service")]
    [InlineData("*")]
    [InlineData("corex-hub-something.service")]
    public void A_stop_is_refused_for_any_unit_the_hub_did_not_create(string candidate)
    {
        Assert.False(SystemdRunCommand.TryBuildStop(candidate, out var argv, out var problem));
        Assert.Empty(argv);
        Assert.Contains("will not be stopped", problem);
    }

    [Fact]
    public void No_signal_or_process_id_appears_in_the_control_surface()
    {
        Assert.True(SystemdRunCommand.TryBuildStop(Unit, out var stop, out _));

        Assert.DoesNotContain(stop, a => a.Contains("kill", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stop, a => a.StartsWith("-s", StringComparison.Ordinal));
        Assert.DoesNotContain(stop, a => a.Contains("SIGKILL", StringComparison.Ordinal));
    }
}
