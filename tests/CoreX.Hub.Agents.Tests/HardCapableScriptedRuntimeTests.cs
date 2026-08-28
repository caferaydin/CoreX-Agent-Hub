using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// That a scripted run means the same thing wherever it ran.
///
/// The hard path does not call <see cref="IAgentRuntime.RunAsync"/> - it cannot, because that
/// method launches a process on whichever machine it is called from, which for a vendor adapter
/// would be the developer's. It asks the runtime what to run, runs it somewhere else, and asks the
/// runtime what the answer means.
///
/// That split is only safe if both halves are the runtime's own. These tests are the proof: the
/// command the isolated path is given is the command the ordinary path builds, and the result it
/// produces is built by the same interpretation, field for field. A runtime whose two paths
/// disagreed would be a hard organization behaving differently from every test that proved the
/// pipeline, which is the kind of difference nobody finds until it matters.
/// </summary>
public sealed class HardCapableScriptedRuntimeTests
{
    private static ScriptedAgentRuntime Runtime(ScriptedAgentMode mode) =>
        new(
            new ScriptedAgentOptions
            {
                FixtureExecutablePath = Path.Combine(Path.GetTempPath(), "corex-fixture.exe"),
                Mode = mode,
            },
            new RecordingProcessRunner());

    private static AgentRunRequest Request(string? structuredOutputPath = null) => new()
    {
        WorkingDirectory = "/srv/corex/exec/workspace",
        SystemPromptFile = "/srv/corex/exec/context/system.md",
        UserPrompt = "do the thing",
        PermissionProfile = Domain.Catalog.AgentPermissionProfile.WorkspaceWrite,
        HardTimeout = TimeSpan.FromMinutes(5),
        CorrelationId = "corex-correlation",
        StructuredOutputPath = structuredOutputPath,
    };

    /// <summary>The scripted runtime is eligible for hard execution; that is a type fact.</summary>
    [Fact]
    public void The_scripted_runtime_is_hard_capable() =>
        Assert.True(HardAgentRuntimeCompatibility.IsHardCapable(Runtime(ScriptedAgentMode.ApplyPatch)));

    /// <summary>
    /// The described command is the command the ordinary path would have run.
    ///
    /// Established by running the ordinary path against a recording runner and comparing what it
    /// asked for against what the description says. Two independently written vectors that agreed
    /// today would be two vectors that could stop agreeing.
    /// </summary>
    [Theory]
    [InlineData(ScriptedAgentMode.ApplyPatch)]
    [InlineData(ScriptedAgentMode.NoOp)]
    [InlineData(ScriptedAgentMode.SecretWrite)]
    [InlineData(ScriptedAgentMode.OutOfScopeWrite)]
    [InlineData(ScriptedAgentMode.UnexpectedCommit)]
    public async Task The_described_command_is_what_the_ordinary_path_runs(ScriptedAgentMode mode)
    {
        var recorder = new RecordingProcessRunner();

        var runtime = new ScriptedAgentRuntime(
            new ScriptedAgentOptions
            {
                FixtureExecutablePath = Path.Combine(Path.GetTempPath(), "corex-fixture.exe"),
                Mode = mode,
            },
            recorder);

        var request = Request();

        await runtime.RunAsync(request, new Progress<AgentEvent>(_ => { }), CancellationToken.None);

        var described = runtime.DescribeCommand(request);

        Assert.NotNull(recorder.Observed);
        Assert.Equal(recorder.Observed!.Executable, described.Executable);
        Assert.Equal(recorder.Observed.Arguments, described.Arguments);
    }

    /// <summary>The structured output path travels with the command, so the caller can fetch it.</summary>
    [Fact]
    public void A_requested_structured_output_path_is_described()
    {
        var described = Runtime(ScriptedAgentMode.ApplyPatch)
            .DescribeCommand(Request("/srv/corex/exec/workspace/.corex/result.json"));

        Assert.Equal("/srv/corex/exec/workspace/.corex/result.json", described.StructuredOutputPath);
        Assert.Contains("--structured-output", described.Arguments);
        Assert.Contains("/srv/corex/exec/workspace/.corex/result.json", described.Arguments);
    }

    /// <summary>
    /// Interpretation is the runtime's, so the same process means the same result either way.
    ///
    /// Every field the orchestrator later reads is compared, not just the outcome: an exit code
    /// that survived while the diagnostic did not would still be a hard execution reporting
    /// something a logical one would not.
    /// </summary>
    [Theory]
    [InlineData(AgentProcessTermination.Exited, 0, AgentRunOutcome.Completed)]
    [InlineData(AgentProcessTermination.Exited, 1, AgentRunOutcome.Failed)]
    [InlineData(AgentProcessTermination.Exited, -1, AgentRunOutcome.Crashed)]
    [InlineData(AgentProcessTermination.TimedOut, null, AgentRunOutcome.TimedOut)]
    [InlineData(AgentProcessTermination.Cancelled, null, AgentRunOutcome.Cancelled)]
    [InlineData(AgentProcessTermination.StartFailed, null, AgentRunOutcome.Crashed)]
    public async Task An_isolated_result_matches_the_ordinary_result_field_for_field(
        AgentProcessTermination termination, int? exitCode, AgentRunOutcome expected)
    {
        var startedAt = DateTimeOffset.UnixEpoch;

        var process = new AgentProcessResult
        {
            Termination = termination,
            ExitCode = exitCode,
            StartedAtUtc = startedAt,
            EndedAtUtc = startedAt + TimeSpan.FromSeconds(3),
            Stdout = "out",
            Stderr = "err",
            StartFailureMessage = termination is AgentProcessTermination.StartFailed ? "nothing started" : null,
        };

        var ordinary = new ScriptedAgentRuntime(
            new ScriptedAgentOptions
            {
                FixtureExecutablePath = Path.Combine(Path.GetTempPath(), "corex-fixture.exe"),
                Mode = ScriptedAgentMode.ApplyPatch,
            },
            new RecordingProcessRunner(process));

        var request = Request();

        var host = await ordinary.RunAsync(request, new Progress<AgentEvent>(_ => { }), CancellationToken.None);

        var isolated = ordinary.Interpret(
            request,
            new HardAgentProcessResult
            {
                Termination = process.Termination,
                ExitCode = process.ExitCode,
                Stdout = process.Stdout,
                Stderr = process.Stderr,
                Duration = process.Duration,
                StartFailureMessage = process.StartFailureMessage,
                StructuredOutput = null,
            });

        Assert.Equal(expected, host.Outcome);
        Assert.Equal(host.Outcome, isolated.Outcome);
        Assert.Equal(host.ExitCode, isolated.ExitCode);
        Assert.Equal(host.Stdout, isolated.Stdout);
        Assert.Equal(host.Stderr, isolated.Stderr);
        Assert.Equal(host.Duration, isolated.Duration);
        Assert.Equal(host.Diagnostic, isolated.Diagnostic);
        Assert.Equal(host.StructuredOutput, isolated.StructuredOutput);
        Assert.Equal(host.ProviderSessionId, isolated.ProviderSessionId);
        Assert.Equal(host.Usage, isolated.Usage);
    }

    /// <summary>
    /// A structured document fetched across the boundary is carried through unchanged.
    ///
    /// The isolated path cannot read the file itself - it is on another filesystem - so it reads it
    /// through the boundary and hands the content back. What matters is that the runtime's contract
    /// still holds: a document was produced, and it is the one the process wrote.
    /// </summary>
    [Fact]
    public void A_structured_document_survives_the_boundary()
    {
        var isolated = Runtime(ScriptedAgentMode.ApplyPatch).Interpret(
            Request("/srv/corex/exec/workspace/.corex/result.json"),
            new HardAgentProcessResult
            {
                Termination = AgentProcessTermination.Exited,
                ExitCode = 0,
                Duration = TimeSpan.FromSeconds(1),
                StructuredOutput = "{\"summary\":\"done\"}",
            });

        Assert.Equal(AgentRunOutcome.Completed, isolated.Outcome);
        Assert.Equal("{\"summary\":\"done\"}", isolated.StructuredOutput);
    }

    /// <summary>A runner that records what it was asked to run and answers with a fixed result.</summary>
    private sealed class RecordingProcessRunner : IAgentProcessRunner
    {
        private readonly AgentProcessResult _result;

        public RecordingProcessRunner(AgentProcessResult? result = null) =>
            _result = result ?? new AgentProcessResult
            {
                Termination = AgentProcessTermination.Exited,
                ExitCode = 0,
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                EndedAtUtc = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1),
            };

        public AgentProcessRequest? Observed { get; private set; }

        public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken ct)
        {
            Observed = request;
            return Task.FromResult(_result);
        }
    }
}
