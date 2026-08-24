using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The runtime end to end against a deterministic stand-in executable.
///
/// Everything but the executable is production: the composition adapter, the process
/// runner, the job object, the empty based environment, stdin and the output pump. So
/// these prove what the operating system really delivered and what the adapter really made
/// of what came back - without a network, an account or a paid request.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeRuntimeTests
{
    [Fact]
    public async Task A_normal_run_completes_and_the_session_is_correlated()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("success");

        var request = fixture.Request();
        var result = await fixture.Runtime().RunAsync(request, new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(request.SessionId, result.ProviderSessionId);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task The_operating_system_receives_the_argument_vector_the_adapter_states()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("success");

        var request = fixture.Request();
        await fixture.Runtime().RunAsync(request, new TimedEventCollector(), CancellationToken.None);

        var delivered = fixture.DeliveredArguments();

        Assert.Contains("-p", delivered);
        Assert.Contains("--verbose", delivered);
        Assert.Contains("--no-session-persistence", delivered);
        Assert.Contains("--safe-mode", delivered);
        Assert.Contains("--strict-mcp-config", delivered);
        Assert.Contains("Read,Edit,Write,Glob,Grep", delivered);
        Assert.Contains("acceptEdits", delivered);
        Assert.Contains("claude-opus-5", delivered);
        Assert.Contains("high", delivered);
        Assert.Contains(request.SessionId, delivered);

        Assert.DoesNotContain(delivered, a => a.Contains("Bash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--add-dir", delivered);
        Assert.DoesNotContain("--bare", delivered);
        Assert.DoesNotContain("--fallback-model", delivered);
        Assert.DoesNotContain("--dangerously-skip-permissions", delivered);
    }

    [Fact]
    public async Task The_prompt_arrives_on_stdin_and_is_absent_from_the_command_line()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("success");

        const string prompt = "SENSITIVE-TASK-TEXT nobody else on this machine should read";

        await fixture.Runtime().RunAsync(
            fixture.Request(userPrompt: prompt), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(prompt, fixture.DeliveredStandardInput());
        Assert.DoesNotContain(
            fixture.DeliveredArguments(),
            a => a.Contains("SENSITIVE-TASK-TEXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_stream_is_read_while_the_run_is_still_going()
    {
        // Streaming is declared, so it is proved: the opening event has to reach the progress
        // channel well before the child exits, not be reconstructed once it has.
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("slow");

        var events = new TimedEventCollector();

        var result = await fixture.Runtime().RunAsync(fixture.Request(), events, CancellationToken.None);
        var finishedAt = DateTimeOffset.UtcNow;

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);

        var firstProviderEvent = events.Events.First(e =>
            e.Event.Message.Contains("provider session", StringComparison.Ordinal));

        Assert.True(
            finishedAt - firstProviderEvent.At > TimeSpan.FromMilliseconds(500),
            "the opening event should have been observed while the run was still going");
    }

    [Fact]
    public async Task Raw_output_survives_a_stream_the_adapter_could_not_read()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("malformed-then-result");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        // A structurally valid final result is present and the process exited cleanly. It is
        // still not a completed run, because part of the account of it could not be read.
        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("could not be read in full", result.Diagnostic!, StringComparison.Ordinal);

        // And the evidence is all there, unparsed and unredacted.
        Assert.Contains("this is not json", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"result\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"subtype\":\"init\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_exit_with_no_final_result_is_not_a_completed_run()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("no-result");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no final result event", result.Diagnostic!, StringComparison.Ordinal);

        // The assistant said something encouraging. That is not a machine outcome.
        Assert.Contains("i said something and then stopped", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stated_provider_error_fails_however_encouraging_the_text_was()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("is-error");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("error_during_execution", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nonzero_exit_beats_a_successful_looking_stream()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("nonzero-exit");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("\"subtype\":\"success\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_that_died_is_a_crash_and_its_partial_output_is_kept()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("partial-then-crash");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Crashed, result.Outcome);
        Assert.Contains("halfway through", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("init-session-mismatch", "rather than the requested")]
    [InlineData("result-session-mismatch", "rather than the requested")]
    public async Task A_session_that_is_not_the_one_that_was_asked_for_fails_closed(string scenario, string expected)
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario(scenario);

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains(expected, result.Diagnostic!, StringComparison.Ordinal);

        // Raw evidence of the mismatch is preserved rather than corrected.
        Assert.Contains("11111111-2222-3333-4444-555555555555", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unverified_session_is_never_reported_as_the_provider_session()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("init-session-mismatch");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Null(result.ProviderSessionId);
    }

    [Fact]
    public async Task A_run_that_never_said_which_session_it_started_fails_closed()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("no-init");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("no session init event", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permission_mode_other_than_the_one_stated_fails_closed()
    {
        // The user settings this run loads carry a default. If one ever won, this is what
        // catches it.
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("wrong-permission-mode");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("bypassPermissions", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_surface_other_than_the_frozen_one_fails_closed()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("extra-tool");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("Bash", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_context_server_that_became_active_fails_closed()
    {
        // Plugins being listed as metadata is not activation. A server that actually became
        // active is outside the contract this runtime states, so the run does not stand.
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("mcp-active");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("context servers", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unmodelled_event_type_does_not_fail_a_run()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("unknown-event");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Contains("a_future_event", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unmodelled_property_does_not_fail_a_run()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("unknown-property");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task A_rate_limit_status_before_a_successful_result_is_still_a_successful_run()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("rate-limit-then-success");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Contains("rate limit", result.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_one_model_reaches_the_result_intact()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("multi-model");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Usage.ModelUsage.Count);
        Assert.Equal(120, result.Usage.InputTokens);
        Assert.Equal(900, result.Usage.CacheReadTokens);
        Assert.Equal(0.0425m, result.Usage.CostAmount);
        Assert.Equal("USD", result.Usage.CostCurrency);
    }

    [Fact]
    public async Task Only_text_blocks_become_assistant_text()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("mixed-content");

        var events = new TimedEventCollector();
        var result = await fixture.Runtime().RunAsync(fixture.Request(), events, CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);

        var text = string.Concat(events.Events
            .Where(e => e.Event.Kind == AgentEventKind.Output)
            .Select(e => e.Event.Message));

        Assert.Equal("first sentence. second sentence.", text);
        Assert.DoesNotContain("THIS IS REASONING", text, StringComparison.Ordinal);
        Assert.DoesNotContain("THIS IS TOOL OUTPUT", text, StringComparison.Ordinal);

        // The raw stream still holds all of it: nothing is dropped from the evidence.
        Assert.Contains("THIS IS TOOL OUTPUT", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_goes_through_the_shared_runner_and_reports_itself_honestly()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("hang");

        using var cancelling = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(hardTimeout: TimeSpan.FromMinutes(5)), new TimedEventCollector(), cancelling.Token);

        Assert.Equal(AgentRunOutcome.Cancelled, result.Outcome);

        // Whatever it had already said is kept; being stopped does not make it a success.
        Assert.Contains("\"subtype\":\"init\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_outlives_its_hard_deadline_reports_a_timeout()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("hang");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(hardTimeout: TimeSpan.FromSeconds(2)), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task An_unsupported_request_is_refused_without_starting_anything()
    {
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("success");

        foreach (var request in new[]
                 {
                     fixture.Request(resumeSessionId: Guid.NewGuid().ToString("D")),
                     fixture.Request(structuredOutputSchema: "{\"type\":\"object\"}"),
                     fixture.Request(permissionProfile: AgentPermissionProfile.ReadOnly),
                     fixture.Request(model: null),
                     fixture.Request(effort: "enormous"),
                     fixture.Request(sessionId: "not-a-uuid"),
                 })
        {
            var result = await fixture.Runtime().RunAsync(request, new TimedEventCollector(), CancellationToken.None);

            Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
            Assert.Null(result.ExitCode);
            Assert.Empty(fixture.DeliveredArguments());
        }
    }

    [Fact]
    public async Task A_prompt_file_that_does_not_exist_is_refused_before_launch()
    {
        using var fixture = ClaudeCodeFixture.Create();

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(systemPromptFile: Path.Combine(fixture.ContextPath, "absent.md")),
            new TimedEventCollector(),
            CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Contains("does not exist", result.Diagnostic!, StringComparison.Ordinal);
        Assert.Empty(fixture.DeliveredArguments());
    }

    [Fact]
    public async Task The_child_never_sees_a_provider_credential_or_this_process_own_session()
    {
        // Read from a real child rather than from a dictionary: the parent of these tests is
        // frequently itself a signed in provider session, so the variables below are usually
        // really set here, and "the child does not see them" has to be observed.
        using var fixture = ClaudeCodeFixture.Create();

        string[] dangerous =
        [
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
            "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID",
            "CLAUDECODE", "CLAUDE_PID", "AWS_ACCESS_KEY_ID", "GOOGLE_APPLICATION_CREDENTIALS",
            "AZURE_CLIENT_SECRET", "GITHUB_TOKEN", "GH_TOKEN",
        ];

        foreach (var name in dangerous)
        {
            Environment.SetEnvironmentVariable(name, "PARENT-VALUE-" + name);
        }

        try
        {
            var result = await fixture.Processes.RunAsync(
                new AgentProcessRequest
                {
                    Executable = ScriptedFixture.FixtureExecutable,
                    Arguments = ["print-env"],
                    WorkingDirectory = fixture.WorkPath,

                    // Exactly what the adapter permits a run to grant.
                    EnvironmentAllowlist = Agents.ClaudeCode.ClaudeCodeInvocationBuilder.PermittedEnvironmentNames,
                    HardTimeout = TimeSpan.FromSeconds(30),
                },
                CancellationToken.None);

            Assert.Equal(AgentProcessTermination.Exited, result.Termination);
            Assert.Equal(0, result.ExitCode);

            foreach (var name in dangerous)
            {
                Assert.DoesNotContain(name, result.Stdout, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("PARENT-VALUE-", result.Stdout, StringComparison.Ordinal);
            }

            // Not vacuous: the child really did receive the narrow set it was granted.
            Assert.Contains("USERPROFILE=", result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SystemRoot=", result.Stdout, StringComparison.OrdinalIgnoreCase);

            // And the G6-0 git baseline is still applied on top of whatever the runtime asked
            // for, so an agent child's git reads the Hub's configuration and not a developer's.
            Assert.Contains("GIT_CONFIG_NOSYSTEM=1", result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GIT_TERMINAL_PROMPT=0", result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var name in dangerous)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public async Task What_the_agent_said_about_itself_never_decides_the_outcome()
    {
        // The stream says "success" in a sentence and fails structurally. If assistant text
        // were ever consulted, this is the run that would pass.
        using var fixture = ClaudeCodeFixture.Create();
        fixture.UseScenario("no-result");

        var result = await fixture.Runtime().RunAsync(
            fixture.Request(), new TimedEventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);

        // The outcome type carries no verdict vocabulary at all, so there is nowhere for a
        // sentence to become one.
        foreach (var forbidden in new[] { "Accepted", "Passed", "Verification", "Review" })
        {
            Assert.DoesNotContain(
                typeof(AgentRunResult).GetProperties(),
                p => p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Health_reports_a_missing_installation_rather_than_pretending()
    {
        using var fixture = ClaudeCodeFixture.Create();

        var health = await fixture.Runtime(Path.Combine(fixture.Root, "absent.exe"))
            .CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
        Assert.Contains("does not exist", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_refuses_an_installation_that_cannot_report_a_version()
    {
        // The stand-in answers a print run, not a version probe, which is exactly the shape
        // of "installed, but not the command surface this adapter was written against".
        using var fixture = ClaudeCodeFixture.Create();

        var health = await fixture.Runtime().CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
    }
}
