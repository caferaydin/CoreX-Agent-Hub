using System.Runtime.Versioning;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Isolation;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// An environment whose every answer the test writes, and which records what it was asked.
///
/// The executor's job is a sequence of decisions - check the identity, check the directory,
/// check the name is free, launch, and stop the unit if anything went wrong - and each of those
/// has a failure the operator's own machine cannot be made to produce without damaging it. So
/// the environment is scripted and the executor is real.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ScriptedExecutionEnvironment : IWslExecutionGateway
{
    private readonly Dictionary<string, WslCallResult> _answers = new(StringComparer.Ordinal);

    /// <summary>Every command this environment was asked to run, in order.</summary>
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>The Linux identity each call entered as, in order.</summary>
    public List<string> Identities { get; } = [];

    /// <summary>An environment where every precondition holds and the workload exits 0.</summary>
    public static ScriptedExecutionEnvironment Correct()
    {
        var script = new ScriptedExecutionEnvironment();

        script.Answer("/usr/bin/id -u corex-agent", "1000");
        script.Answer("/usr/bin/stat -c %A /srv/corex/exec/run-1", "drwxrwx---");
        script.AnswerPrefix("/usr/bin/systemctl show ", "LoadState=not-found");
        script.AnswerPrefix("/usr/bin/systemd-run ", string.Empty);
        script.AnswerPrefix("/usr/bin/systemctl stop ", string.Empty);

        return script;
    }

    public ScriptedExecutionEnvironment Answer(string command, string stdout, int exitCode = 0)
    {
        _answers[command] = new WslCallResult(
            WslCallTermination.Exited, exitCode, stdout, string.Empty, false, false, null);

        return this;
    }

    /// <summary>Answers any command starting with this text; unit names carry a fresh guid.</summary>
    public ScriptedExecutionEnvironment AnswerPrefix(string prefix, string stdout, int exitCode = 0)
    {
        _answers["prefix:" + prefix] = new WslCallResult(
            WslCallTermination.Exited, exitCode, stdout, string.Empty, false, false, null);

        return this;
    }

    public ScriptedExecutionEnvironment AnswerRaw(string prefix, WslCallResult result)
    {
        _answers["prefix:" + prefix] = result;
        return this;
    }

    public Task<WslCallResult> RunAsync(string environmentId, WslCall call, CancellationToken ct)
    {
        Calls.Add(call.Arguments);
        Identities.Add(call.LinuxUser);

        var signature = string.Join(' ', call.Arguments);

        if (_answers.TryGetValue(signature, out var exact))
        {
            return Task.FromResult(exact);
        }

        foreach (var (key, value) in _answers)
        {
            if (key.StartsWith("prefix:", StringComparison.Ordinal)
                && signature.StartsWith(key["prefix:".Length..], StringComparison.Ordinal))
            {
                return Task.FromResult(value);
            }
        }

        // A command the script has no answer for is reported as never started, which is how the
        // real gateway reports one it could not run.
        return Task.FromResult(WslCallResult.NotStarted("no scripted answer."));
    }
}

/// <summary>
/// What the executor does with what the environment says.
///
/// Two properties matter most here and neither is visible in the argv tests. The first is that
/// every precondition is established before a workload is created, so a systemd status in its
/// own reserved range can never be mistaken for a workload's exit code. The second is that an
/// abnormal ending always stops the unit: the host supervisor ending a <c>wsl.exe</c> process
/// says nothing about the Linux service it launched, and a cancellation that only did the former
/// would report success at stopping a process tree that is still running.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardProcessExecutorTests
{
    private static HardIsolationProfile Profile()
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

        Assert.True(HardIsolationProfile.TryRead(config, out var profile, out var problem), problem);
        return profile!;
    }

    private static HardProcessRequest Request() => new()
    {
        Executable = "/usr/bin/id",
        Arguments = ["-u"],
        WorkingDirectory = "/srv/corex/exec/run-1",
        HardTimeout = TimeSpan.FromMinutes(5),
        CorrelationId = Guid.CreateVersion7(),
    };

    private static Task<HardProcessResult> RunAsync(
        ScriptedExecutionEnvironment script, HardProcessRequest? request = null) =>
        WslHardProcessExecutor.Scripted(script)
            .RunAsync(Profile(), request ?? Request(), CancellationToken.None);

    // -- the ordinary path ------------------------------------------------------------------

    [Fact]
    public async Task A_correct_environment_runs_the_workload_and_reports_its_exit_code()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(HardUnitName.IsHubOwned(result.UnitName));
        Assert.Null(result.StartFailureMessage);
    }

    /// <summary>
    /// The trusted control plane enters as root; the untrusted workload does not run as root.
    ///
    /// Both halves are asserted together because either one alone would be misleading. Entering
    /// as root is necessary - creating a systemd unit is privileged - and it is only safe because
    /// the workload inside that unit is given a different identity by <c>--uid</c>.
    /// </summary>
    [Fact]
    public async Task The_control_identity_is_root_and_the_workload_identity_is_not()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        await RunAsync(script);

        Assert.All(script.Identities, identity => Assert.Equal("root", identity));

        var launch = script.Calls.Single(c => c[0] == "/usr/bin/systemd-run");

        Assert.Contains("--uid=corex-agent", launch);
        Assert.DoesNotContain("--uid=root", launch);
    }

    /// <summary>Nothing is asked of the environment before the request has been judged.</summary>
    [Fact]
    public async Task A_request_the_command_builder_refuses_never_reaches_the_environment()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        var result = await WslHardProcessExecutor.Scripted(script).RunAsync(
            Profile(),
            Request() with { WorkingDirectory = "/etc" },
            CancellationToken.None);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Empty(script.Calls);
    }

    // -- preconditions ----------------------------------------------------------------------

    /// <summary>
    /// The agent identity is resolved first, and a failure is a refusal rather than a run.
    ///
    /// systemd would answer this with exit status 217, which is indistinguishable from a workload
    /// that exited 217. Asking beforehand is what makes the exit code trustworthy afterwards.
    /// </summary>
    [Fact]
    public async Task An_agent_user_the_environment_cannot_resolve_stops_the_run()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .Answer("/usr/bin/id -u corex-agent", string.Empty, exitCode: 1);

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("no account 'corex-agent'", result.StartFailureMessage);
        Assert.DoesNotContain(script.Calls, c => c[0] == "/usr/bin/systemd-run");
    }

    /// <summary>
    /// An agent user that resolves to root is refused, however the profile spells it.
    ///
    /// A configured account named something reassuring can still be uid 0, and the name is not
    /// the thing that matters.
    /// </summary>
    [Fact]
    public async Task An_agent_user_that_is_really_root_stops_the_run()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .Answer("/usr/bin/id -u corex-agent", "0");

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("may not be root", result.StartFailureMessage);
        Assert.DoesNotContain(script.Calls, c => c[0] == "/usr/bin/systemd-run");
    }

    [Fact]
    public async Task A_working_directory_that_does_not_exist_stops_the_run()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .Answer("/usr/bin/stat -c %A /srv/corex/exec/run-1", string.Empty, exitCode: 1);

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("does not exist", result.StartFailureMessage);
        Assert.DoesNotContain(script.Calls, c => c[0] == "/usr/bin/systemd-run");
    }

    [Fact]
    public async Task A_working_directory_that_is_a_file_stops_the_run()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .Answer("/usr/bin/stat -c %A /srv/corex/exec/run-1", "-rw-rw----");

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("is not a directory", result.StartFailureMessage);
    }

    /// <summary>
    /// A unit name already in use is a refusal, not something to attach to.
    ///
    /// The name carries a fresh correlation id, so a collision means the unit belongs to somebody
    /// else - and reusing it would put this run's ceilings and this run's cancellation authority
    /// over work the Hub did not start.
    /// </summary>
    [Fact]
    public async Task A_unit_name_already_in_use_stops_the_run()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .AnswerPrefix("/usr/bin/systemctl show ", "LoadState=loaded");

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Contains("already exists", result.StartFailureMessage);
        Assert.DoesNotContain(script.Calls, c => c[0] == "/usr/bin/systemd-run");
    }

    [Fact]
    public async Task An_environment_that_cannot_answer_stops_the_run()
    {
        var script = new ScriptedExecutionEnvironment();

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.DoesNotContain(script.Calls, c => c[0] == "/usr/bin/systemd-run");
    }

    /// <summary>Preconditions are asked in the order that produces the most useful answer.</summary>
    [Fact]
    public async Task The_preconditions_are_asked_before_the_workload_is_created()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        await RunAsync(script);

        var executables = script.Calls.Select(c => c[0]).ToList();

        Assert.Equal(
            ["/usr/bin/id", "/usr/bin/stat", "/usr/bin/systemctl", "/usr/bin/systemd-run"], executables);
    }

    // -- ending a run -----------------------------------------------------------------------

    /// <summary>
    /// A workload that ended on its own needs no stop; the unit collected itself.
    ///
    /// Worth asserting, because a stop issued on every run would hide a missing stop on the runs
    /// that actually need one.
    /// </summary>
    [Fact]
    public async Task A_workload_that_finished_is_not_stopped_again()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        await RunAsync(script);

        Assert.DoesNotContain(script.Calls, c => c.Count > 1 && c[1] == "stop");
    }

    /// <summary>
    /// Every abnormal ending stops the unit, because ending the host process does not.
    ///
    /// This is the case the whole design turns on: <c>wsl.exe</c> dying leaves the Linux service
    /// running, so a cancellation that stopped only the host process would report a cancelled run
    /// while the work carried on inside the environment.
    /// </summary>
    [Theory]
    [InlineData("Cancelled", HardProcessOutcome.Cancelled)]
    [InlineData("HardTimeout", HardProcessOutcome.HardTimeout)]
    [InlineData("SoftTimeout", HardProcessOutcome.SoftTimeout)]
    [InlineData("IdleTimeout", HardProcessOutcome.IdleTimeout)]
    public async Task An_abnormal_ending_stops_the_whole_unit(
        string termination, HardProcessOutcome expected)
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .AnswerRaw(
                "/usr/bin/systemd-run ",
                new WslCallResult(
                    Enum.Parse<WslCallTermination>(termination),
                    null,
                    string.Empty,
                    string.Empty,
                    false,
                    false,
                    null));

        var result = await RunAsync(script);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.ExitCode);

        var stop = script.Calls.Single(c => c.Count > 1 && c[1] == "stop");

        Assert.Equal(["/usr/bin/systemctl", "stop", result.UnitName], stop);
    }

    /// <summary>
    /// The stop names this run's own unit and nothing else.
    ///
    /// There is no path from a task, an API parameter or an agent's output to this argument: the
    /// name was derived from a Hub correlation id and the stop refuses anything that was not.
    /// </summary>
    [Fact]
    public async Task Cancellation_targets_only_this_runs_unit()
    {
        var request = Request();
        var script = ScriptedExecutionEnvironment.Correct()
            .AnswerRaw(
                "/usr/bin/systemd-run ",
                new WslCallResult(
                    WslCallTermination.Cancelled, null, string.Empty, string.Empty, false, false, null));

        var result = await RunAsync(script, request);

        Assert.Equal(HardUnitName.For(request.CorrelationId), result.UnitName);
        Assert.Equal(["/usr/bin/systemctl", "stop", HardUnitName.For(request.CorrelationId)],
            script.Calls.Single(c => c.Count > 1 && c[1] == "stop"));
    }

    // -- results ----------------------------------------------------------------------------

    [Fact]
    public async Task A_nonzero_workload_exit_is_reported_as_the_workload_ending()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .AnswerRaw(
                "/usr/bin/systemd-run ",
                new WslCallResult(WslCallTermination.Exited, 3, "out", "err", false, false, null));

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.Exited, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Stdout);
        Assert.Equal("err", result.Stderr);
    }

    /// <summary>The two deadlines are reported together, so neither is invisible.</summary>
    [Fact]
    public async Task The_result_carries_the_environments_own_runtime_ceiling()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        var result = await RunAsync(script, Request() with { HardTimeout = TimeSpan.FromSeconds(120) });

        Assert.Equal(135, result.EnvironmentRuntimeCeilingSeconds);
    }

    // -- no fallback ------------------------------------------------------------------------

    /// <summary>
    /// Nothing runs anywhere else when the environment cannot run it.
    ///
    /// Every refusal above is a refusal, not a redirection: there is no second attempt, no
    /// alternative identity, and no host side execution. The proof is that the only commands ever
    /// issued go into the environment, and the failing ones issue none at all.
    /// </summary>
    [Fact]
    public async Task A_refused_run_is_never_attempted_somewhere_weaker()
    {
        var script = ScriptedExecutionEnvironment.Correct()
            .Answer("/usr/bin/id -u corex-agent", string.Empty, exitCode: 1);

        var result = await RunAsync(script);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.False(result.Started);
        Assert.Null(result.ExitCode);

        // One question was asked and answered no; nothing was launched, here or anywhere.
        Assert.Single(script.Calls);
    }

    /// <summary>
    /// An executor with no environment to reach refuses; it does not invent a result.
    ///
    /// This is the fabrication a fallback would look like from the outside - an exit code of
    /// zero, no unit, nothing having run - and it is indistinguishable from success to every
    /// caller. The construction here fails the way it would on a machine with no WSL: there is
    /// no usable working directory, so there is no gateway.
    /// </summary>
    [Fact]
    public async Task An_executor_with_no_environment_refuses_rather_than_inventing_a_result()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), "corex-absent", Guid.CreateVersion7().ToString("N"), "nowhere");

        var result = await WslHardProcessExecutor.Create(missing)
            .RunAsync(Profile(), Request(), CancellationToken.None);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.False(result.Started);
        Assert.Null(result.ExitCode);
        Assert.NotNull(result.StartFailureMessage);
    }

    /// <summary>
    /// A backend this build does not run in is refused before the environment is contacted.
    /// </summary>
    [Fact]
    public async Task An_unsupported_backend_is_refused()
    {
        var script = ScriptedExecutionEnvironment.Correct();

        var result = await WslHardProcessExecutor.Scripted(script).RunAsync(
            Profile() with { Backend = (HardIsolationBackend)99 },
            Request(),
            CancellationToken.None);

        Assert.Equal(HardProcessOutcome.StartFailed, result.Outcome);
        Assert.Empty(script.Calls);
    }
}
