using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The Codex adapter's own decisions: which build it will run, what it refuses, and what it
/// makes of a stream.
///
/// The version policy is proved through the process port rather than against the two Codex
/// builds that happen to be installed here. What matters is the rule - run the configured
/// path, accept exactly one version, never look anywhere else - and a test that depended on
/// a particular machine's installation would stop proving the rule the moment that machine
/// changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CodexRuntimeTests
{

    [Fact]
    public void The_declared_capabilities_are_what_this_adapter_implements()
    {
        var capabilities = Runtime(new ScriptedCodexProcesses()).Describe();

        Assert.True(capabilities.SupportsStructuredSchema);
        Assert.True(capabilities.SupportsReadOnlyMode);
        Assert.True(capabilities.SupportsStreaming);

        Assert.False(capabilities.SupportsSessionResume);
        Assert.False(capabilities.SupportsCostCap);

        // The run really does execute with most of the provider's surface switched off, but
        // the caller cannot state a list and have it honoured - which is what this capability
        // promises. Claiming it would be a lie told in the caller's direction.
        Assert.False(capabilities.SupportsToolAllowlist);
    }

    // --- Which build runs -------------------------------------------------------------------

    [Fact]
    public async Task An_executable_that_is_not_there_is_never_replaced_by_one_that_is()
    {
        using var lab = CodexLab.Create();

        var runtime = new CodexRuntime(
            CodexRequests.Options(Path.Combine(lab.Root, "absent-codex.exe"), lab.Root),
            new ScriptedCodexProcesses());

        var health = await runtime.CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
        Assert.Contains("does not exist", health.Detail, StringComparison.Ordinal);

        // The point of the message: there is a working Codex on PATH on this machine, and
        // this runtime is saying it will not go looking for it.
        Assert.Contains("PATH", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pinned_build_is_accepted()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming("codex-cli 0.130.0\n");

        var health = await Installed(lab, processes).CheckHealthAsync(CancellationToken.None);

        Assert.True(health.IsHealthy, health.Detail);
        Assert.Contains("0.130.0", health.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex-cli 0.149.1")]
    [InlineData("codex-cli 0.131.0")]
    [InlineData("codex-cli 0.129.0")]
    public async Task A_different_build_at_the_configured_path_is_refused_rather_than_tried(string reported)
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(reported + "\n");

        var health = await Installed(lab, processes).CheckHealthAsync(CancellationToken.None);

        // Not a floor and not a range. The behaviour a review depends on moved between
        // releases in both directions, so a newer build is a different runtime rather than a
        // better one, and an older one is not a safer bet either.
        Assert.False(health.IsHealthy, health.Detail);
        Assert.Contains("accepts 0.130.0 exactly", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_probe_runs_the_configured_path_and_no_other()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming("codex-cli 0.130.0\n");
        var installed = Path.Combine(lab.Root, "codex.exe");

        await Installed(lab, processes).CheckHealthAsync(CancellationToken.None);

        // There is a working Codex on PATH on a developer machine, and it is not this one.
        // The probe names an absolute path so nothing can resolve to it.
        var probe = Assert.Single(processes.Requests);

        Assert.Equal(installed, probe.Executable);
        Assert.Contains("--version", probe.Arguments);
    }

    [Fact]
    public async Task An_executable_that_cannot_report_a_version_is_not_trusted()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming("something else entirely\n");

        var health = await Installed(lab, processes).CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
        Assert.Contains("no recognisable version", health.Detail, StringComparison.Ordinal);
    }

    // --- What it refuses before launching anything -------------------------------------------

    [Theory]
    [InlineData("write")]
    [InlineData("resume")]
    [InlineData("structured-path")]
    [InlineData("effort")]
    public async Task An_unsupported_request_is_refused_without_starting_anything(string shape)
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses();

        var request = CodexRequests.Review(lab.WorkPath, r => shape switch
        {
            "write" => r with { PermissionProfile = AgentPermissionProfile.WorkspaceWrite },
            "resume" => r with { ResumeSessionId = CodexStreams.Thread },
            "structured-path" => r with { StructuredOutputPath = Path.Combine(lab.Root, "out.json") },
            _ => r with { EffortHint = "ultra" },
        }, lab.SystemPromptPath);

        var result = await Runtime(processes).RunAsync(request, Sink(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Diagnostic);
        Assert.Empty(processes.Requests);
    }

    [Fact]
    public async Task A_missing_hub_contract_stops_the_run_before_the_provider_sees_it()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses();

        var result = await Runtime(processes).RunAsync(
            CodexRequests.Review(lab.WorkPath, null, Path.Combine(lab.Root, "no-such-contract.md")),
            Sink(),
            CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("only source of the reviewer's instructions", result.Diagnostic!, StringComparison.Ordinal);
        Assert.Empty(processes.Requests);
    }

    // --- What it makes of a run ---------------------------------------------------------------

    [Fact]
    public async Task A_finished_turn_is_a_completed_run_attributed_to_the_provider_thread()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(CodexStreams.Success("all good"));

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(CodexStreams.Thread, result.ProviderSessionId);
        Assert.Equal(13047, result.Usage.InputTokens);
        Assert.Contains("thread.started", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_hub_correlation_is_never_reported_as_the_provider_thread()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(CodexStreams.Success());

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        // This provider names its own thread and cannot be told which one to use. Reporting
        // the Hub's identifier back would produce something that looks like proof of an
        // attribution nobody observed.
        Assert.NotEqual("0199aa00-0000-7000-8000-000000000001", result.ProviderSessionId);
        Assert.Equal(CodexStreams.Thread, result.ProviderSessionId);
    }

    [Fact]
    public async Task A_clean_exit_with_no_terminal_event_is_not_a_completed_run()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(
            CodexStreams.ThreadStarted() + CodexStreams.TurnStarted() + CodexStreams.Message("half a review"));

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        // Exit zero is not the authority. A run whose ending nobody can point to has not
        // finished, and calling it Completed would let a truncated review pass as a whole one.
        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no terminal event", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_turn_the_provider_never_named_cannot_be_attributed()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(
            CodexStreams.TurnStarted() + CodexStreams.Message("done") + CodexStreams.TurnCompleted());

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Null(result.ProviderSessionId);
    }

    [Fact]
    public async Task An_authentication_failure_is_a_failed_run_whose_evidence_survives()
    {
        using var lab = CodexLab.Create();

        var stream = CodexStreams.ThreadStarted()
                     + CodexStreams.Error("Your access token could not be refreshed")
                     + CodexStreams.TurnFailed("Your access token could not be refreshed");

        var processes = new ScriptedCodexProcesses().Streaming(stream, exitCode: 1);

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("access token", result.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(stream, result.Stdout);
        Assert.Null(result.StructuredOutput);
    }

    [Theory]
    [InlineData(AgentProcessTermination.TimedOut, AgentRunOutcome.TimedOut)]
    [InlineData(AgentProcessTermination.Cancelled, AgentRunOutcome.Cancelled)]
    [InlineData(AgentProcessTermination.StartFailed, AgentRunOutcome.Crashed)]
    public async Task Process_truth_wins_over_anything_the_stream_said(
        AgentProcessTermination termination,
        AgentRunOutcome expected)
    {
        using var lab = CodexLab.Create();

        // A success shaped stream that was then killed is not a successful run.
        var processes = new ScriptedCodexProcesses().Ending(termination, CodexStreams.Success());

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);

        // The partial evidence still survives.
        Assert.Contains("thread.started", result.Stdout, StringComparison.Ordinal);
    }

    // --- Structured output ---------------------------------------------------------------------

    [Fact]
    public async Task A_structured_answer_is_returned_exactly_as_it_arrived()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(
            CodexStreams.Success("{\"marker\":\"OK\",\"findings\":[]}"));

        var result = await Runtime(processes).RunAsync(
            Request(lab, r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }),
            Sink(),
            CancellationToken.None);

        Assert.Equal("{\"marker\":\"OK\",\"findings\":[]}", result.StructuredOutput);
    }

    [Fact]
    public async Task No_schema_means_no_structured_result_is_claimed()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(CodexStreams.Success("just prose"));

        var result = await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        // The final message is prose here. Handing it back as a structured document would
        // invent a shape nobody asked for.
        Assert.Null(result.StructuredOutput);
    }

    [Fact]
    public async Task A_failed_run_never_carries_a_structured_result()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(
            CodexStreams.ThreadStarted() + CodexStreams.Message("{\"marker\":\"OK\"}"));

        var result = await Runtime(processes).RunAsync(
            Request(lab, r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }),
            Sink(),
            CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Null(result.StructuredOutput);
    }

    // --- The temporary schema file ---------------------------------------------------------------

    [Fact]
    public async Task The_schema_is_written_outside_the_reviewed_tree_without_a_byte_order_mark()
    {
        using var lab = CodexLab.Create();

        string? schemaPath = null;
        byte[]? firstBytes = null;

        var processes = new ScriptedCodexProcesses().Streaming(CodexStreams.Success("{}"));

        var runtime = new CodexRuntime(
            CodexRequests.Options(Path.Combine(lab.Root, "codex.exe"), lab.Root),
            new InspectingProcesses(processes, request =>
            {
                var index = request.Arguments.ToList().IndexOf("--output-schema");
                schemaPath = index >= 0 ? request.Arguments[index + 1] : null;

                if (schemaPath is not null && File.Exists(schemaPath))
                {
                    firstBytes = File.ReadAllBytes(schemaPath)[..3];
                }
            }));

        await runtime.RunAsync(
            Request(lab, r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }),
            Sink(),
            CancellationToken.None);

        Assert.NotNull(schemaPath);

        // Measured against the pinned release: it refuses a schema file that starts with a
        // byte order mark, and says only that the json is invalid at line 1.
        Assert.NotNull(firstBytes);
        Assert.NotEqual<byte[]>([0xEF, 0xBB, 0xBF], firstBytes!);

        // A file written inside the snapshot would be a change to the thing under review.
        Assert.False(schemaPath!.StartsWith(lab.WorkPath, StringComparison.OrdinalIgnoreCase));

        // And it does not outlive the run.
        Assert.False(File.Exists(schemaPath));
    }

    [Theory]
    [InlineData(AgentProcessTermination.Exited)]
    [InlineData(AgentProcessTermination.TimedOut)]
    [InlineData(AgentProcessTermination.Cancelled)]
    [InlineData(AgentProcessTermination.StartFailed)]
    public async Task The_schema_is_removed_however_the_run_ended(AgentProcessTermination termination)
    {
        using var lab = CodexLab.Create();

        string? schemaPath = null;

        var inner = new ScriptedCodexProcesses().Ending(termination, CodexStreams.Success());

        var runtime = new CodexRuntime(
            CodexRequests.Options(Path.Combine(lab.Root, "codex.exe"), lab.Root),
            new InspectingProcesses(inner, request =>
            {
                var index = request.Arguments.ToList().IndexOf("--output-schema");
                schemaPath = index >= 0 ? request.Arguments[index + 1] : null;
            }));

        await runtime.RunAsync(
            Request(lab, r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }),
            Sink(),
            CancellationToken.None);

        Assert.NotNull(schemaPath);
        Assert.False(File.Exists(schemaPath));
    }

    [Fact]
    public async Task Two_runs_never_share_a_schema_file()
    {
        using var lab = CodexLab.Create();

        var seen = new List<string>();

        for (var i = 0; i < 2; i++)
        {
            var inner = new ScriptedCodexProcesses().Streaming(CodexStreams.Success("{}"));

            var runtime = new CodexRuntime(
                CodexRequests.Options(Path.Combine(lab.Root, "codex.exe"), lab.Root),
                new InspectingProcesses(inner, request =>
                {
                    var index = request.Arguments.ToList().IndexOf("--output-schema");

                    if (index >= 0)
                    {
                        seen.Add(request.Arguments[index + 1]);
                    }
                }));

            await runtime.RunAsync(
                Request(lab, r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }),
                Sink(),
                CancellationToken.None);
        }

        Assert.Equal(2, seen.Count);
        Assert.NotEqual(seen[0], seen[1]);
    }

    // --- The prompt ----------------------------------------------------------------------------

    [Fact]
    public async Task The_hub_contract_reaches_the_provider_on_standard_input()
    {
        using var lab = CodexLab.Create();

        var processes = new ScriptedCodexProcesses().Streaming(CodexStreams.Success());

        await Runtime(processes).RunAsync(Request(lab), Sink(), CancellationToken.None);

        var stdin = processes.LastStandardInput;

        Assert.NotNull(stdin);
        Assert.Contains("REVIEWER CONTRACT MARKER", stdin!, StringComparison.Ordinal);
        Assert.Contains("Review the result state", stdin!, StringComparison.Ordinal);

        // Neither half is on the command line.
        Assert.DoesNotContain(
            processes.Requests[^1].Arguments,
            a => a.Contains("REVIEWER CONTRACT MARKER", StringComparison.Ordinal));
    }

    private static CodexRuntime Runtime(IAgentProcessRunner processes) =>
        new(CodexRequests.Options(@"C:\cx\_tools\codex-0.130.0\codex.exe", Path.GetTempPath()), processes);

    /// <summary>A runtime whose configured executable really is on disk, so health gets past the file check.</summary>
    private static CodexRuntime Installed(CodexLab lab, IAgentProcessRunner processes)
    {
        var executable = Path.Combine(lab.Root, "codex.exe");

        if (!File.Exists(executable))
        {
            File.WriteAllText(executable, "not a real executable, only a path that exists");
        }

        return new CodexRuntime(CodexRequests.Options(executable, lab.Root), processes);
    }

    private static AgentRunRequest Request(
        CodexLab lab,
        Func<AgentRunRequest, AgentRunRequest>? mutate = null) =>
        CodexRequests.Review(lab.WorkPath, mutate, lab.SystemPromptPath);

    private static IProgress<AgentEvent> Sink() => new Progress<AgentEvent>(_ => { });

}

/// <summary>Watches what the adapter asked the process boundary for, then delegates.</summary>
public sealed class InspectingProcesses : IAgentProcessRunner
{
    private readonly IAgentProcessRunner _inner;
    private readonly Action<AgentProcessRequest> _inspect;

    public InspectingProcesses(IAgentProcessRunner inner, Action<AgentProcessRequest> inspect)
    {
        _inner = inner;
        _inspect = inspect;
    }

    public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken)
    {
        _inspect(request);
        return _inner.RunAsync(request, cancellationToken);
    }
}

/// <summary>A disposable layout with a Hub owned reviewer contract in it.</summary>
public sealed class CodexLab : IDisposable
{
    private CodexLab(string root)
    {
        Root = root;
        WorkPath = Path.Combine(root, "work");
        SystemPromptPath = Path.Combine(root, "reviewer.md");

        Directory.CreateDirectory(WorkPath);
        File.WriteAllText(SystemPromptPath, "# REVIEWER CONTRACT MARKER\nDo not modify any file.\n");
    }

    public string Root { get; }

    public string WorkPath { get; }

    public string SystemPromptPath { get; }

    public static CodexLab Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-codex-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);

        return new CodexLab(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
