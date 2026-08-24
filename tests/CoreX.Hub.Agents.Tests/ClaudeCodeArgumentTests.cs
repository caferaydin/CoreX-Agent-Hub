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
    public void The_child_inherits_the_organization_grant_narrowed_to_what_this_runtime_permits()
    {
        // The organization's list covers every managed child it owns, including a
        // verification toolchain whose NuGet restore needs names this runtime has no use
        // for. Those are dropped rather than passed on: the child sees the intersection,
        // which is never more than either side allowed.
        var granted = ClaudeCodeFixture.DefaultEnvironmentAllowlist;

        Assert.Contains("APPDATA", granted);
        Assert.Contains("ProgramFiles", granted);

        var inherited = ClaudeCodeInvocationBuilder.Plan(Request()).Invocation!.EnvironmentAllowlist;

        Assert.Equal(
            ClaudeCodeInvocationBuilder.PermittedEnvironmentNames.Order(StringComparer.OrdinalIgnoreCase),
            inherited.Order(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("APPDATA", inherited);
        Assert.DoesNotContain("ProgramFiles", inherited);

        // And nothing the runtime needs was lost on the way.
        foreach (var required in ClaudeCodeInvocationBuilder.RequiredEnvironmentNames)
        {
            Assert.Contains(required, inherited);
        }
    }

    [Fact]
    public void A_grant_narrower_than_the_ceiling_is_passed_on_as_it_is()
    {
        // Narrowing only ever removes. A caller that granted less than the ceiling gets
        // exactly what it granted, not the ceiling.
        string[] granted = ["SystemRoot", "TEMP", "TMP", "USERPROFILE"];

        var inherited = ClaudeCodeInvocationBuilder
            .Plan(Request(r => r with { EnvironmentAllowlist = granted }))
            .Invocation!
            .EnvironmentAllowlist;

        Assert.Equal(granted, inherited);
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
    public void A_name_the_runtime_has_no_use_for_is_narrowed_away_rather_than_refused()
    {
        // A harmless extra is not an error - the organization grants one list for every
        // child it owns - but it never reaches this one either.
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            EnvironmentAllowlist = [.. ClaudeCodeFixture.DefaultEnvironmentAllowlist, "COMSPEC", "DOTNET_ROOT"],
        }));

        Assert.False(plan.IsRefused, plan.Refusal);
        Assert.DoesNotContain("COMSPEC", plan.Invocation!.EnvironmentAllowlist);
        Assert.DoesNotContain("DOTNET_ROOT", plan.Invocation.EnvironmentAllowlist);
    }

    [Fact]
    public void Narrowing_never_becomes_a_way_of_tolerating_a_credential()
    {
        // The distinction the narrowing turns on: a name this runtime does not need is
        // dropped, and a name no child may ever inherit is still a refusal, so a credential
        // can never be quietly filtered out and forgotten about.
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            EnvironmentAllowlist = [.. ClaudeCodeFixture.DefaultEnvironmentAllowlist, "ANTHROPIC_API_KEY"],
        }));

        Assert.True(plan.IsRefused);
        Assert.Contains("ANTHROPIC_API_KEY", plan.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_capabilities_are_refused_before_launch_rather_than_dropped()
    {
        foreach (var (mutate, expected) in new (Func<AgentRunRequest, AgentRunRequest>, string)[]
                 {
                     (r => r with { ResumeSessionId = Guid.NewGuid().ToString("D") }, "resume"),
                     (r => r with { StructuredOutputPath = "C:/tmp/out.json" }, "path"),
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

        // Genuinely implemented as of the reviewer mode: a schema is passed on and the
        // structured result comes back, and a read only run really does run with reading
        // tools under plan permission.
        Assert.True(capabilities.SupportsStructuredSchema);
        Assert.True(capabilities.SupportsReadOnlyMode);

        // Not claimed, and each refusal above is what "not claimed" costs the caller.
        Assert.False(capabilities.SupportsCostCap);
        Assert.False(capabilities.SupportsSessionResume);
    }


    // ------------------------------------------------------------------- reviewer ------

    [Fact]
    public void A_read_only_request_asks_for_reading_tools_and_plan_permission()
    {
        var arguments = Plan(r => r with { PermissionProfile = AgentPermissionProfile.ReadOnly });

        Assert.Equal("Read,Glob,Grep", Value(arguments, "--tools"));
        Assert.Equal("plan", Value(arguments, "--permission-mode"));

        // Nothing that can change a file or run a command is offered to a reviewer.
        foreach (var forbidden in new[] { "Edit", "Write", "Bash" })
        {
            Assert.DoesNotContain(
                arguments,
                a => a.Split(',').Contains(forbidden, StringComparer.Ordinal));
        }

        Assert.DoesNotContain("acceptEdits", arguments);
    }

    [Fact]
    public void A_supplied_schema_reaches_the_provider()
    {
        const string Schema = "{\"type\":\"object\"}";

        var arguments = Plan(r => r with
        {
            PermissionProfile = AgentPermissionProfile.ReadOnly,
            StructuredOutputSchema = Schema,
        });

        Assert.Equal(Schema, Value(arguments, "--json-schema"));
    }

    [Fact]
    public void No_schema_means_no_schema_flag()
    {
        Assert.DoesNotContain("--json-schema", Plan());
    }

    [Fact]
    public void The_expected_effective_tools_include_the_one_the_provider_adds_itself()
    {
        // Measured in the Phase 7A gate: with --json-schema the provider reports a
        // StructuredOutput tool it was never asked for. Treating that as a breach would
        // refuse every structured review; treating any extra tool as fine would miss a shell.
        Assert.Equal(
            ["Read", "Glob", "Grep", "StructuredOutput"],
            ClaudeCodeInvocationBuilder.ExpectedEffectiveTools(
                AgentPermissionProfile.ReadOnly, structuredSchemaRequested: true));

        Assert.Equal(
            ["Read", "Glob", "Grep"],
            ClaudeCodeInvocationBuilder.ExpectedEffectiveTools(
                AgentPermissionProfile.ReadOnly, structuredSchemaRequested: false));

        Assert.Equal(
            ["Read", "Edit", "Write", "Glob", "Grep"],
            ClaudeCodeInvocationBuilder.ExpectedEffectiveTools(
                AgentPermissionProfile.WorkspaceWrite, structuredSchemaRequested: false));
    }

    [Fact]
    public void An_implementer_request_is_unchanged_by_the_reviewer_mode_existing()
    {
        // ADR-0020 is not relaxed because a second mode arrived.
        var arguments = Plan();

        Assert.Equal("Read,Edit,Write,Glob,Grep", Value(arguments, "--tools"));
        Assert.Equal("acceptEdits", Value(arguments, "--permission-mode"));
        Assert.DoesNotContain(arguments, a => a.Contains("Bash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_structured_output_path_is_still_refused_even_though_a_schema_is_supported()
    {
        var plan = ClaudeCodeInvocationBuilder.Plan(Request(r => r with
        {
            PermissionProfile = AgentPermissionProfile.ReadOnly,
            StructuredOutputSchema = "{\"type\":\"object\"}",
            StructuredOutputPath = "C:/tmp/out.json",
        }));

        Assert.True(plan.IsRefused);
        Assert.Contains("path", plan.Refusal!, StringComparison.OrdinalIgnoreCase);
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
