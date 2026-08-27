using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The process plane against a real WSL environment.
///
/// Everything the argv tests prove is about what was asked for. These are about what the machine
/// actually did with it: whether the workload really ran as another identity, whether the
/// ceilings really bound it, and whether cancelling really ended a tree of processes rather than
/// the one the Hub could see.
///
/// Two things this deliberately does not claim. It does not make the distribution it runs in
/// hard-ready - that machine fails eleven of the twenty-one readiness checks and still does, as
/// the last test here re-establishes. And it does not make a whole Hub task isolated; a contained
/// process is one part of that, and the rest is still on the host.
///
/// On a machine with no WSL the fixture reports as unavailable and each test says so rather than
/// failing, because the absence of a distribution is not a defect in this code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslHardProcessTests : IAsyncLifetime
{
    private readonly WslHardProcessFixture _wsl = new();

    public async Task InitializeAsync() => await _wsl.InitializeAsync();

    public async Task DisposeAsync() => await _wsl.DisposeAsync();

    private async Task<HardProcessResult> RunAsync(
        HardProcessRequest request,
        HardIsolationProfile? profile = null,
        CancellationToken ct = default) =>
        await _wsl.Executor().RunAsync(profile ?? _wsl.Profile(), request, ct);

    // -- the run itself ---------------------------------------------------------------------

    /// <summary>A transient service runs the workload and its exit code comes back.</summary>
    [Fact]
    public async Task A_transient_service_executes_the_workload()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var result = await RunAsync(_wsl.Workload("streams", "0"));

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(HardUnitName.IsHubOwned(result.UnitName));
    }

    /// <summary>
    /// The workload runs as the account the profile named, and not as the account that launched it.
    ///
    /// The host side entered the distribution as root - it had to, creating a systemd unit is
    /// privileged - and the thing inside the unit is somebody else entirely.
    /// </summary>
    [Fact]
    public async Task The_workload_runs_as_the_configured_agent_and_not_as_root()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var result = await RunAsync(_wsl.Workload("identity"));

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.DoesNotContain("UID=0", result.Stdout, StringComparison.Ordinal);

        var (_, expected) = await _wsl.ControlAsync(["/usr/bin/id", "-u", WslHardProcessFixture.AgentUser]);

        Assert.Contains("UID=" + expected.Trim(), result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_workload_starts_in_the_requested_working_directory()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var result = await RunAsync(_wsl.Workload("identity"));

        Assert.Contains("CWD=" + _wsl.Root, result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Both streams come back, separately, and the workload's exit code with them.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Both_streams_are_captured_alongside_the_exit_code(int exitCode)
    {
        if (!_wsl.Available)
        {
            return;
        }

        var result = await RunAsync(
            _wsl.Workload("streams", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("ON-STDOUT", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("ON-STDERR", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ON-STDERR", result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Standard input reaches the workload and its end is observable.
    ///
    /// Worth checking rather than assuming: the pipe runs from the Hub, through wsl.exe, through
    /// systemd-run and into a service, and an end-of-stream that stopped anywhere along the way
    /// would leave a workload waiting for input that never ends.
    /// </summary>
    [Fact]
    public async Task Standard_input_reaches_the_workload_and_ends()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var request = _wsl.Workload("echo") with { StdinPayload = "corex-stdin-marker" };

        var result = await RunAsync(request);

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("READ=corex-stdin-marker", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("EOF-SEEN", result.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing of the Windows caller reaches the workload, and what was asked for does.
    ///
    /// The negative half is the interesting one. The Hub's own environment carries a Windows PATH
    /// and, on a developer machine, whatever provider variables happen to be set; none of it
    /// crosses, because the request carries values rather than names to inherit and WSL forwards
    /// only what WSLENV names.
    /// </summary>
    [Fact]
    public async Task The_workload_environment_is_the_environments_own_plus_exactly_what_was_asked_for()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var request = _wsl.Workload("environment") with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["COREX_MARKER"] = "present",
            },
        };

        var result = await RunAsync(request);

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Contains("COREX_MARKER=present", result.Stdout, StringComparison.Ordinal);

        Assert.DoesNotContain("/mnt/c", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ANTHROPIC", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENAI", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USERPROFILE", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("APPDATA", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// HOME is the Linux account's own, never the Windows caller's.
    ///
    /// Nothing fabricates it: systemd resolves it from the passwd entry of the uid the service
    /// runs as, which is why the value is a Linux path belonging to the agent account.
    /// </summary>
    [Fact]
    public async Task Home_comes_from_the_linux_identity()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var result = await RunAsync(_wsl.Workload("environment"));

        var home = result.Stdout
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("HOME=", StringComparison.Ordinal));

        Assert.NotNull(home);
        Assert.StartsWith("HOME=/", home, StringComparison.Ordinal);
        Assert.DoesNotContain(":", home, StringComparison.Ordinal);
    }

    // -- ceilings ---------------------------------------------------------------------------

    /// <summary>
    /// The process ceiling really bounds the tree, and the workload itself counts towards it.
    ///
    /// The same semantic as the Windows Job Object: a ceiling of three means the workload plus
    /// two, not the workload plus three. Measured rather than assumed, and paired with a control
    /// run so a test that always failed to fork would not look like enforcement.
    /// </summary>
    [Fact]
    public async Task The_process_ceiling_bounds_the_tree()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var bounded = await RunAsync(_wsl.Workload("spawn", "10"), _wsl.Profile(processLimit: 3));
        var control = await RunAsync(_wsl.Workload("spawn", "10"), _wsl.Profile(processLimit: 64));

        Assert.Equal(HardProcessOutcome.Exited, bounded.Outcome);
        Assert.Contains("SPAWNED=2", bounded.Stdout, StringComparison.Ordinal);

        Assert.Equal(HardProcessOutcome.Exited, control.Outcome);
        Assert.Contains("SPAWNED=10", control.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The memory ceiling really stops the workload, and the control run really reaches the target.
    ///
    /// Small on purpose - 64 MiB against a 192 MiB target, in 4 MiB steps. No multi-gigabyte
    /// allocation and no swap pressure: the question is whether the ceiling holds, and a bigger
    /// experiment would answer the same question while being unkind to the machine.
    /// </summary>
    [Fact]
    public async Task The_memory_ceiling_stops_the_workload()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var bounded = await RunAsync(
            _wsl.Workload("alloc", "192"), _wsl.Profile(memoryLimitBytes: 64L * 1024 * 1024));

        var control = await RunAsync(
            _wsl.Workload("alloc", "192"), _wsl.Profile(memoryLimitBytes: 512L * 1024 * 1024));

        Assert.DoesNotContain("REACHED=192", bounded.Stdout, StringComparison.Ordinal);
        Assert.NotEqual(0, bounded.ExitCode);

        Assert.Equal(HardProcessOutcome.Exited, control.Outcome);
        Assert.Equal(0, control.ExitCode);
        Assert.Contains("REACHED=192", control.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host deadline ends a run, and ends the whole unit with it.
    ///
    /// Ending the <c>wsl.exe</c> process would have left the Linux service running, so what this
    /// really checks is the explicit stop that follows: after the deadline the unit is gone from
    /// the environment, not merely gone from the Hub's view.
    /// </summary>
    [Fact]
    public async Task The_host_deadline_ends_the_run_and_the_unit()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var request = _wsl.Workload("sleep", "300") with { HardTimeout = TimeSpan.FromSeconds(5) };

        var stopwatch = Stopwatch.StartNew();
        var result = await RunAsync(request);
        stopwatch.Stop();

        Assert.Equal(HardProcessOutcome.HardTimeout, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"took {stopwatch.Elapsed}");

        Assert.Empty(await _wsl.CgroupMembersAsync(result.UnitName));
    }

    /// <summary>
    /// The environment bounds its own work when the Hub is not there to do it.
    ///
    /// This is the property the two deadlines exist for, and it is the one production cannot
    /// demonstrate synchronously: on the ordinary path the host always notices first. So the test
    /// launches the same unit the production builder describes - same ceilings, same kill mode,
    /// same runtime property - detached, so that nothing on the Windows side is watching it, and
    /// then waits for the environment to end it on its own.
    /// </summary>
    [Fact]
    public async Task The_environment_bounds_an_abandoned_run_by_itself()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var request = _wsl.Workload("sleep", "300") with { HardTimeout = TimeSpan.FromSeconds(1) };
        var unit = HardUnitName.For(request.CorrelationId);

        Assert.True(
            SystemdRunCommand.TryBuild(_wsl.Profile(), request, unit, out var argv, out var problem), problem);

        // The production vector waits for the workload; an abandoned run is what happens when
        // nothing waits, so the two waiting options are replaced and everything else - the
        // identity, the working directory, all four properties - is exactly what production asks
        // for.
        var detached = argv
            .Select(a => a == "--wait" ? "--no-block" : a)
            .Where(a => a != "--pipe")
            .ToList();

        Assert.Contains(
            "--property=RuntimeMaxSec=" + SystemdRunCommand.RuntimeSeconds(request.HardTimeout), detached);

        var (launched, _) = await _wsl.ControlAsync(detached);
        Assert.Equal(0, launched);

        Assert.NotEmpty(await _wsl.CgroupMembersAsync(unit));

        // 1s deadline plus the 15s grace, and room for systemd to act on it.
        await Task.Delay(TimeSpan.FromSeconds(25));

        Assert.Empty(await _wsl.CgroupMembersAsync(unit));
        Assert.Equal("LoadState=not-found", await _wsl.UnitLoadStateAsync(unit));
    }

    // -- cancellation -----------------------------------------------------------------------

    /// <summary>
    /// Cancelling ends every process in the run, not the one the Hub happens to know about.
    ///
    /// The workload is a parent, a child and a grandchild, and the evidence is control group
    /// membership rather than process names: killing by name would find the wrong processes on a
    /// busy machine and would prove nothing about containment on a quiet one.
    /// </summary>
    [Fact]
    public async Task Cancelling_ends_the_whole_control_group()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var request = _wsl.Workload("tree") with { HardTimeout = TimeSpan.FromMinutes(5) };
        var unit = HardUnitName.For(request.CorrelationId);

        using var cancellation = new CancellationTokenSource();

        var run = RunAsync(request, ct: cancellation.Token);

        var members = await WaitForMembersAsync(unit, atLeast: 3);
        Assert.Equal(3, members.Count);

        await cancellation.CancelAsync();

        var result = await run;

        Assert.Equal(HardProcessOutcome.Cancelled, result.Outcome);
        Assert.Empty(await _wsl.CgroupMembersAsync(unit));

        // Every process that was in the group is gone, checked by the ids that were in it rather
        // than by anything's name.
        foreach (var pid in members)
        {
            var (exitCode, _) = await _wsl.ControlAsync(["/usr/bin/stat", "-c", "%n", "/proc/" + pid]);
            Assert.NotEqual(0, exitCode);
        }
    }

    /// <summary>The unit collects itself; nothing is left loaded for an operator to clear.</summary>
    [Fact]
    public async Task The_transient_unit_leaves_nothing_behind()
    {
        if (!_wsl.Available)
        {
            return;
        }

        var succeeded = await RunAsync(_wsl.Workload("streams", "0"));
        var failed = await RunAsync(_wsl.Workload("streams", "3"));

        Assert.Equal("LoadState=not-found", await _wsl.UnitLoadStateAsync(succeeded.UnitName));
        Assert.Equal("LoadState=not-found", await _wsl.UnitLoadStateAsync(failed.UnitName));

        // And no unit file was installed anywhere either; a transient unit exists only in memory.
        var (exitCode, _) = await _wsl.ControlAsync(
            ["/usr/bin/stat", "-c", "%n", "/etc/systemd/system/" + succeeded.UnitName]);

        Assert.NotEqual(0, exitCode);
    }

    // -- what this does not change ----------------------------------------------------------

    /// <summary>
    /// The distribution these tests ran in is still not hard-ready.
    ///
    /// A process plane working there proves that systemd and cgroups are available, and that is
    /// all it proves. The machine still mounts the host's drives, still registers Windows interop
    /// and still has no isolated agent account, and readiness still says so.
    /// </summary>
    [Fact]
    public async Task Running_here_does_not_make_this_environment_hard_ready()
    {
        if (!_wsl.Available)
        {
            return;
        }

        await RunAsync(_wsl.Workload("streams", "0"));

        using var temp = new TempWorkingDirectory();

        var readiness = await WslReadinessProbe.Create(temp.Path).ProbeAsync(
            "fixture", _wsl.Profile(), CancellationToken.None);

        Assert.False(readiness.IsReady);
        Assert.NotEmpty(readiness.UnmetCheckIds);
    }

    /// <summary>
    /// Admission still refuses, and the process plane existing is why that is worth asserting.
    ///
    /// The temptation this guards against is small and specific: a contained process now works,
    /// so it would be easy to conclude that hard execution works. A whole task still prepares its
    /// workspace, context, git, verification, review and evidence on the host.
    /// </summary>
    [Fact]
    public void A_working_process_plane_does_not_make_hard_execution_available()
    {
        var plane = new UnavailableHardExecutionPlane();

        Assert.False(plane.IsAvailable);
        Assert.Contains("on the host", plane.UnavailableReason, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<string>> WaitForMembersAsync(string unit, int atLeast)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var members = await _wsl.CgroupMembersAsync(unit);

            if (members.Count >= atLeast)
            {
                return members;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return await _wsl.CgroupMembersAsync(unit);
    }

    private sealed class TempWorkingDirectory : IDisposable
    {
        public TempWorkingDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "corex-hard-readiness", Guid.CreateVersion7().ToString("N"));

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
