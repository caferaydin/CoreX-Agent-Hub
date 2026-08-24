using System.Reflection;
using System.Text.RegularExpressions;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.ClaudeCode;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What arriving at a real provider is allowed to change, and what it is not.
///
/// Phase 6 adds a second runtime. That is the only thing it adds: the contract stays three
/// members wide, the orchestrator still cannot tell which runtime it got, the child still
/// starts from the Hub's own environment, and nothing about the shell, the credentials or
/// the money moved into configuration.
///
/// These are shape guards. Behaviour is proved by the adapter tests, which drive the real
/// process runner against a deterministic executable.
/// </summary>
public sealed class AgentRuntimeInvariantTests
{
    [Fact]
    public void Adding_a_real_provider_did_not_widen_the_contract()
    {
        Assert.Equal(
            ["CheckHealthAsync", "Describe", "RunAsync"],
            typeof(IAgentRuntime)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name)
                .Order(StringComparer.Ordinal));

        // Two implementations now, and both implement the same three members. A provider that
        // needed a fourth would have made the other one implement a lie.
        var runtimes = ArchitectureAssemblies.Agents
            .GetTypes()
            .Where(t => typeof(IAgentRuntime).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ClaudeCodeRuntime", "ScriptedAgentRuntime"], runtimes);
    }

    [Fact]
    public void The_real_runtime_is_reachable_only_through_the_shared_interface()
    {
        // Named in the adapter that defines it and in the one composition file that registers
        // it. Nothing else in the system knows the type exists.
        //
        // Matched as the type rather than as the family: the configuration section and the
        // factory carry the same family name and are not uses of the runtime.
        var type = new Regex(@"\bClaudeCodeRuntime\b", RegexOptions.CultureInvariant);

        var namers = RepoLayout.ProductionSourceFiles
            .Where(f => type.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ClaudeCodeRuntime.cs", "ClaudeCodeRuntimeFactory.cs"], namers);

        // And what the orchestration side holds is the interface.
        Assert.Contains(
            "IReadOnlyDictionary<string, IAgentRuntime>",
            Read("AgentGateway.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_the_orchestrator_learns_which_runtime_it_got()
    {
        var pipeline = Read("ExecutionOrchestrator.Pipeline.cs");

        Assert.Contains("_agent.RunAsync(", pipeline, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "Claude", "Codex", "RuntimeKey", "Provider" })
        {
            Assert.DoesNotContain(forbidden, pipeline, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_native_windows_tool_surface_is_five_tools_and_the_shell_is_not_one_of_them()
    {
        // ADR-0020, asserted as a value rather than as a policy sentence.
        Assert.Equal(["Read", "Edit", "Write", "Glob", "Grep"], ClaudeCodeInvocationBuilder.Tools);

        // And no production file anywhere can name the shell as a tool, so no second list and
        // no configuration path can put it back.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            Assert.DoesNotMatch(
                new Regex(@"""Bash""|Bash\(|,Bash|Bash,", RegexOptions.CultureInvariant),
                File.ReadAllText(file));
        }
    }

    [Fact]
    public void The_tool_surface_has_no_configuration_surface_at_all()
    {
        // A list that could be appended to from a file would make ADR-0020 a suggestion.
        var surface = typeof(ClaudeCodeInvocationBuilder)
            .GetProperty(nameof(ClaudeCodeInvocationBuilder.Tools))!;

        Assert.True(surface.GetSetMethod() is null);
        Assert.Null(typeof(ClaudeCodeOptions).GetProperty("Tools"));
        Assert.Null(typeof(Application.Configuration.ClaudeCodeRuntimeConfig).GetProperty("Tools"));

        // The one settings type carries two locations and nothing else.
        Assert.Equal(
            ["ExecutablePath", "HealthProbeDirectory"],
            typeof(ClaudeCodeOptions).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_falls_back_to_another_model_or_another_provider()
    {
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[] { "--fallback-model", "fallbackModel", "--bare", "--dangerously-skip" })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{RepoLayout.Relative(file)}: {forbidden}");
            }
        }
    }

    [Fact]
    public void There_is_no_key_no_token_and_no_credential_store()
    {
        // Authentication is out of band (ADR-0009). Nothing reads a credential file, nothing
        // writes one, and nothing has a place to hold one.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         ".credentials.json", "setup-token", "apiKeyHelper",
                         "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
                     })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{RepoLayout.Relative(file)}: {forbidden}");
            }
        }

        // The adapter never names one of these because it refuses whole families by prefix,
        // which is what makes the scan above pass without an exemption.
        Assert.Contains("ANTHROPIC_", ClaudeCodeInvocationBuilder.ForbiddenEnvironmentPrefixes);
        Assert.Contains("CLAUDE_", ClaudeCodeInvocationBuilder.ForbiddenEnvironmentPrefixes);
        Assert.True(ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName("ANTHROPIC_API_KEY"));
        Assert.True(ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.True(ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName("AWS_SECRET_ACCESS_KEY"));
    }

    [Fact]
    public void The_real_runtime_starts_no_process_of_its_own()
    {
        var adapter = ReadAdapterSources();

        foreach (var forbidden in new[] { "Process.Start", "ProcessStartInfo", "System.Diagnostics" })
        {
            Assert.DoesNotContain(forbidden, adapter, StringComparison.Ordinal);
        }

        // It asks through the Agents owned port, which composition answers with the single
        // launch point.
        Assert.Contains("IAgentProcessRunner", Read("ClaudeCodeRuntime.cs"), StringComparison.Ordinal);
        Assert.Contains("_processes.RunAsync(", Read("ClaudeCodeRuntime.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_identifier_comes_from_the_execution_and_is_never_invented()
    {
        var adapter = ReadAdapterSources();

        foreach (var invention in new[] { "Guid.NewGuid", "Guid.CreateVersion7", "Guid.NewGuid()" })
        {
            Assert.DoesNotContain(invention, adapter, StringComparison.Ordinal);
        }

        // Composition hands the execution's own identifier down as the session.
        Assert.Contains("SessionId = correlationId", Read("AgentGateway.cs"), StringComparison.Ordinal);

        // And the orchestrator's correlation identifier is the execution id.
        Assert.Contains(
            "execution.Id.ToString(\"D\")",
            Read("ExecutionOrchestrator.Pipeline.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_request_is_refused_before_a_process_could_exist()
    {
        var runtime = Read("ClaudeCodeRuntime.cs");

        var refusal = runtime.IndexOf("PreflightRefusal(request) is { } refusal", StringComparison.Ordinal);
        var plan = runtime.IndexOf("ClaudeCodeInvocationBuilder.Plan(request)", StringComparison.Ordinal);
        var launch = runtime.IndexOf("_processes.RunAsync(", StringComparison.Ordinal);

        Assert.True(refusal > 0 && plan > refusal, "the filesystem preflight must precede planning");
        Assert.True(launch > plan, "planning, and every refusal in it, must precede the launch");

        // The planner cannot start anything even if the order were ever changed.
        var planner = Read("ClaudeCodeInvocation.cs");

        foreach (var forbidden in new[] { "RunAsync", "Process", "File.", "Directory." })
        {
            Assert.DoesNotContain(forbidden, planner, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_provider_cost_is_metadata_and_no_ceiling_is_enforced_from_it()
    {
        // Budget policy is provider neutral: concurrency, counters and three deadlines. A
        // provider spend flag would move that decision into someone else's product.
        Assert.False(
            new ClaudeCodeRuntime(
                new ClaudeCodeOptions { ExecutablePath = "x", HealthProbeDirectory = "y" },
                new NullProcessRunner()).Describe().SupportsCostCap);

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            Assert.DoesNotContain("--max-budget-usd", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_neutral_layers_still_know_nothing_about_a_provider()
    {
        foreach (var module in new[]
                 {
                     "CoreX.Hub.Domain", "CoreX.Hub.Workspace", "CoreX.Hub.Verification", "CoreX.Hub.Context",
                 })
        {
            var files = RepoLayout.ProductionSourceFiles
                .Where(f => f.Contains(
                    Path.DirectorySeparatorChar + module + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                var code = string.Join(
                    '\n',
                    File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

                foreach (var provider in new[] { "Claude", "Codex", "Anthropic", "Gemini" })
                {
                    Assert.False(
                        Regex.IsMatch(code, @"\b" + provider + @"\b", RegexOptions.IgnoreCase),
                        $"{RepoLayout.Relative(file)} names {provider}");
                }
            }
        }
    }

    [Fact]
    public void Phase_seven_and_later_work_has_not_arrived_early()
    {
        var declarations = new Regex(
            @"\b(class|record|interface|struct)\s+(ReviewEngine|CodexRuntime|GeminiRuntime|RepairLoop|"
            + @"RepairEngine|BillingAccount|CredentialStore|ProviderFallback)\b",
            RegexOptions.CultureInvariant);

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            Assert.DoesNotMatch(declarations, File.ReadAllText(file));
        }

        // And nothing under src is an API surface or a user interface yet.
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[] { "WebApplication", "ControllerBase", "MapGet(", "IHostedService" })
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.Ordinal),
                    $"{RepoLayout.Relative(file)}: {forbidden}");
            }
        }
    }

    private static string Read(string fileName) =>
        File.ReadAllText(
            RepoLayout.ProductionSourceFiles.Single(f =>
                string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal)));

    /// <summary>Every file of the provider adapter, concatenated.</summary>
    private static string ReadAdapterSources() =>
        string.Join(
            '\n',
            RepoLayout.ProductionSourceFiles
                .Where(f => f.Contains(
                    Path.DirectorySeparatorChar + "ClaudeCode" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

    private sealed class NullProcessRunner : IAgentProcessRunner
    {
        public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("this guard never runs anything");
    }
}
