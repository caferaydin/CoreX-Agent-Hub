using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.ClaudeCode;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The command surface, asserted as a value rather than as an intention.
///
/// Everything a run is allowed to do is decided by this argument vector, so it is checked
/// flag by flag: what must be there, what must never be there, and what must be exactly
/// what the catalog said rather than whatever a default would have produced.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClaudeCodeArgumentTests
{
    private static readonly string[] ExpectedTools = ["Read", "Edit", "Write", "Glob", "Grep"];

    [Fact]
    public void The_native_windows_tool_surface_is_exactly_five_tools_and_no_shell()
    {
        // ADR-0020. The claim is about the effective list, so it is read from the argument
        // vector rather than from the constant that produced it.
        var arguments = Plan();

        Assert.Equal("Read,Edit,Write,Glob,Grep", Value(arguments, "--tools"));

        Assert.Equal(ExpectedTools, ClaudeCodeInvocationBuilder.Tools);

        foreach (var shell in new[] { "Bash", "bash", "Shell", "Terminal", "Exec" })
        {
            Assert.DoesNotContain(
                arguments,
                a => a.Contains(shell, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void No_configuration_surface_can_add_a_tool()
    {
        // The only settings this runtime has are where the executable is and where its health
        // probes stand. There is no tool list, no permission mode and no shell switch to set,
        // so no deployment can put the shell back through a string in a file.
        var settings = typeof(ClaudeCodeOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["ExecutablePath", "HealthProbeDirectory"], settings);

        foreach (var property in typeof(Application.Configuration.ClaudeCodeRuntimeConfig).GetProperties())
        {
            foreach (var forbidden in new[] { "Tool", "Permission", "Shell", "Bash", "Arg", "Flag", "Mode" })
            {
                Assert.False(
                    property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"ClaudeCodeRuntimeConfig.{property.Name} would be a way to change the frozen surface");
            }
        }
    }

    [Fact]
    public void The_stream_contract_is_stated_in_full()
    {
        var arguments = Plan();

        Assert.Contains("-p", arguments);
        Assert.Equal("stream-json", Value(arguments, "--output-format"));

        // 2.1.241 refuses --print with stream-json unless this is present. It was measured,
        // not assumed, and dropping it turns every run into a start failure.
        Assert.Contains("--verbose", arguments);
    }

    [Fact]
    public void The_session_the_execution_asked_for_is_the_session_that_is_requested()
    {
        var session = Guid.NewGuid().ToString("D");

        Assert.Equal(session, Value(Plan(r => r with { SessionId = session }), "--session-id"));
    }

    [Fact]
    public void Session_persistence_is_off_and_no_resume_is_attempted()
    {
        var arguments = Plan();

        Assert.Contains("--no-session-persistence", arguments);
        Assert.DoesNotContain("--resume", arguments);
        Assert.DoesNotContain("-r", arguments);
        Assert.DoesNotContain("--continue", arguments);
        Assert.DoesNotContain("--fork-session", arguments);
    }

    [Fact]
    public void Customisation_is_disabled_and_only_user_settings_are_loaded()
    {
        var arguments = Plan();

        Assert.Contains("--safe-mode", arguments);
        Assert.Equal("user", Value(arguments, "--setting-sources"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--mcp-config", arguments);
    }

    [Fact]
    public void The_permission_mode_is_stated_rather_than_inherited_from_user_settings()
    {
        // The user settings this run deliberately loads carry a default permission mode. If
        // the adapter did not state one, that default would silently decide what the agent
        // may do, which is the one thing --setting-sources user must not be allowed to reach.
        Assert.Equal("acceptEdits", Value(Plan(), "--permission-mode"));

        foreach (var mode in new[] { "bypassPermissions", "auto", "manual", "dontAsk", "plan" })
        {
            Assert.DoesNotContain(mode, Plan());
        }
    }

    [Fact]
    public void The_model_and_the_effort_come_from_the_catalog_and_are_never_defaulted()
    {
        var arguments = Plan(r => r with { ModelHint = "claude-opus-5", EffortHint = "high" });

        Assert.Equal("claude-opus-5", Value(arguments, "--model"));
        Assert.Equal("high", Value(arguments, "--effort"));

        // A model the provider decides for itself would make two runs of the same profile
        // different work, so there is no path to one.
        Assert.DoesNotContain("--fallback-model", arguments);
    }

    [Fact]
    public void An_effort_the_provider_has_no_exact_word_for_is_refused_rather_than_approximated()
    {
        foreach (var effort in new[] { "highest", "very-high", "HIGH ", "med", "3", "" })
        {
            var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with { EffortHint = effort }));

            if (string.Equals(effort.Trim(), "high", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(plan.IsRefused);
                continue;
            }

            Assert.True(plan.IsRefused, $"'{effort}' should not have mapped to anything");
        }

        Assert.Equal(["low", "medium", "high", "xhigh", "max"], ClaudeCodeInvocationBuilder.EffortLevels);
    }

    [Fact]
    public void Both_prompt_files_are_passed_and_the_append_file_is_optional()
    {
        var both = Plan();

        Assert.EndsWith("system.md", Value(both, "--system-prompt-file"), StringComparison.Ordinal);
        Assert.EndsWith("rules.md", Value(both, "--append-system-prompt-file"), StringComparison.Ordinal);

        var contractOnly = Plan(r => r with { AppendSystemPromptFile = null });

        Assert.DoesNotContain("--append-system-prompt-file", contractOnly);
        Assert.EndsWith("system.md", Value(contractOnly, "--system-prompt-file"), StringComparison.Ordinal);
    }

    [Fact]
    public void No_extra_directory_is_granted_to_the_file_tools()
    {
        // The two prompt files are read by the tool itself before the agent exists; nothing
        // needs the context pack directory to be reachable by Edit or Write. Granting it
        // would make Hub owned context writable by the thing it is supposed to constrain.
        Assert.DoesNotContain("--add-dir", Plan());
    }

    [Fact]
    public void Nothing_on_the_command_line_can_change_who_pays_or_what_is_allowed()
    {
        var arguments = Plan();

        foreach (var forbidden in new[]
                 {
                     "--bare", "--fallback-model", "--dangerously-skip-permissions",
                     "--allow-dangerously-skip-permissions", "--settings", "--betas",
                     "--max-budget-usd", "--agents", "--agent", "--plugin-dir", "--plugin-url",
                     "--json-schema", "--worktree", "--cloud", "--chrome", "--ide",
                 })
        {
            Assert.DoesNotContain(forbidden, arguments);
        }

        // No key, no token, no endpoint: authentication is out of band and stays there.
        Assert.DoesNotContain(arguments, a => a.Contains("api", StringComparison.OrdinalIgnoreCase)
                                              && a.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_prompt_travels_on_stdin_and_never_on_the_command_line()
    {
        const string prompt = "a task description nobody else on this machine should be able to read";

        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with { UserPrompt = prompt }));

        Assert.Equal(prompt, plan.Invocation!.StandardInputText);
        Assert.DoesNotContain(plan.Invocation.Arguments, a => a.Contains(prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void The_child_inherits_what_the_organization_granted_and_nothing_more()
    {
        var plan = ClaudeCodeInvocationBuilder.Plan(Request());

        Assert.Equal(ClaudeCodeFixture.DefaultEnvironmentAllowlist, plan.Invocation!.EnvironmentAllowlist);
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_CUSTOM_HEADERS")]
    [InlineData("ANTHROPIC_MODEL")]
    [InlineData("ANTHROPIC_SMALL_FAST_MODEL")]
    [InlineData("ANTHROPIC_DEFAULT_SONNET_MODEL")]
    [InlineData("CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("CLAUDE_CODE_USE_VERTEX")]
    [InlineData("CLAUDE_CODE_USE_FOUNDRY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("CLAUDE_CODE_MESSAGING_TOKEN")]
    [InlineData("CLAUDE_CODE_MESSAGING_SOCKET")]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("CLAUDE_CODE_BRIDGE_SESSION_ID")]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CLAUDE_CODE_ENTRYPOINT")]
    [InlineData("CLAUDE_CODE_EXECPATH")]
    [InlineData("CLAUDECODE")]
    [InlineData("CLAUDE_PID")]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS")]
    [InlineData("AZURE_CLIENT_SECRET")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GH_TOKEN")]
    [InlineData("SSH_ASKPASS")]
    [InlineData("GIT_ASKPASS")]
    public void A_run_that_asks_to_inherit_a_credential_or_a_parent_session_is_refused(string name)
    {
        Assert.True(ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName(name));

        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            EnvironmentAllowlist = [.. ClaudeCodeFixture.DefaultEnvironmentAllowlist, name],
        }));

        Assert.True(plan.IsRefused);
        Assert.Contains(name, plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_forbidden_environment_detector_is_not_fooled_by_the_names_that_are_needed()
    {
        foreach (var needed in ClaudeCodeInvocationBuilder.PermittedEnvironmentNames)
        {
            Assert.False(
                ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName(needed),
                $"'{needed}' is required and must not be classified as dangerous");
        }
    }

    [Fact]
    public void A_run_that_does_not_grant_what_the_runtime_needs_is_refused_rather_than_silently_short()
    {
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            EnvironmentAllowlist = ["SystemRoot", "PATH"],
        }));

        Assert.True(plan.IsRefused);
        Assert.Contains("USERPROFILE", plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_asks_for_more_than_the_runtime_permits_is_refused()
    {
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            EnvironmentAllowlist = [.. ClaudeCodeFixture.DefaultEnvironmentAllowlist, "APPDATA"],
        }));

        Assert.True(plan.IsRefused);
        Assert.Contains("APPDATA", plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_capabilities_are_refused_before_launch_rather_than_dropped()
    {
        foreach (var (mutate, expected) in new (Func<AgentRunRequest, AgentRunRequest>, string)[]
                 {
                     (r => r with { ResumeSessionId = Guid.NewGuid().ToString("D") }, "resume"),
                     (r => r with { StructuredOutputSchema = "{\"type\":\"object\"}" }, "schema"),
                     (r => r with { StructuredOutputPath = "C:/tmp/out.json" }, "structured"),
                     (r => r with { PermissionProfile = AgentPermissionProfile.ReadOnly }, "read only"),
                 })
        {
            var plan = ClaudeCodeInvocationBuilder.Plan(mutate(Request()));

            Assert.True(plan.IsRefused);
            Assert.Contains(expected, plan.Refusal!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_run_without_a_usable_session_identifier_is_refused()
    {
        foreach (var session in new[] { null, "", "   ", "not-a-uuid", "HUB-001#1" })
        {
            var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with { SessionId = session }));

            Assert.True(plan.IsRefused, $"'{session}' should not have been accepted");
        }
    }

    [Fact]
    public void A_run_without_a_model_is_refused()
    {
        foreach (var model in new[] { null, "", "   " })
        {
            Assert.True(ClaudeCodeInvocationBuilder.Plan(Request(r => r with { ModelHint = model })).IsRefused);
        }
    }

    [Fact]
    public void The_declared_capabilities_are_what_this_adapter_implements()
    {
        using var fixture = ClaudeCodeFixture.Create();

        var capabilities = fixture.Runtime().Describe();

        Assert.True(capabilities.SupportsStreaming);
        Assert.True(capabilities.SupportsToolAllowlist);

        // Not claimed, and each refusal above is what "not claimed" costs the caller.
        Assert.False(capabilities.SupportsCostCap);
        Assert.False(capabilities.SupportsSessionResume);
        Assert.False(capabilities.SupportsStructuredSchema);
        Assert.False(capabilities.SupportsReadOnlyMode);
    }

    private static IReadOnlyList<string> Plan(Func<AgentRunRequest, AgentRunRequest>? mutate = null)
    {
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(mutate));

        Assert.False(plan.IsRefused, plan.Refusal);
        return plan.Invocation!.Arguments;
    }

    private static AgentRunRequest Request(Func<AgentRunRequest, AgentRunRequest>? mutate = null)
    {
        var request = new AgentRunRequest
        {
            WorkingDirectory = @"C:\cx\work",
            SystemPromptFile = @"C:\cx\context\system.md",
            AppendSystemPromptFile = @"C:\cx\context\rules.md",
            UserPrompt = "do the work",
            PermissionProfile = AgentPermissionProfile.WorkspaceWrite,
            ModelHint = "claude-opus-5",
            EffortHint = "high",
            EnvironmentAllowlist = ClaudeCodeFixture.DefaultEnvironmentAllowlist,
            HardTimeout = TimeSpan.FromMinutes(30),
            CorrelationId = "HUB-001#1",
            SessionId = Guid.NewGuid().ToString("D"),
        };

        return mutate is null ? request : mutate(request);
    }

    /// <summary>The value that follows a flag, asserting the flag appears exactly once.</summary>
    private static string Value(IReadOnlyList<string> arguments, string flag)
    {
        var index = -1;

        for (var i = 0; i < arguments.Count; i++)
        {
            if (!string.Equals(arguments[i], flag, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(index < 0, $"{flag} appears more than once");
            index = i;
        }

        Assert.True(index >= 0, $"{flag} is missing");
        Assert.True(index + 1 < arguments.Count, $"{flag} has no value");

        return arguments[index + 1];
    }
}
