using System.Text.RegularExpressions;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What a second provider may and may not do to the rest of the system, expressed as shape.
///
/// Phase 8 passes only when swapping the reviewer is a configuration change. That property is
/// invisible in behaviour - a Codex branch inside the orchestrator would work perfectly well
/// and would still have destroyed the thing being built - so it is guarded here, where adding
/// one has to break a test.
///
/// The other half is the pin. This provider changed the behaviour a review depends on between
/// releases, so a runtime that would run whichever build it found is not a runtime, and the
/// absence of a fallback is worth asserting rather than trusting.
/// </summary>
public sealed class CodexInvariantTests
{
    [Fact]
    public void The_runtime_contract_did_not_grow_to_fit_a_second_provider()
    {
        // ADR-0008. A member added here to accommodate one provider makes every other
        // provider implement a lie, and the reviewer swap is exactly the moment that
        // temptation arrives.
        var members = typeof(IAgentRuntime).GetMembers()
            .Where(m => m.DeclaringType == typeof(IAgentRuntime))
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["CheckHealthAsync", "Describe", "RunAsync"], members);
    }

    [Fact]
    public void No_sequencing_or_decision_code_knows_this_provider_exists()
    {
        // The whole point of Phase 8: the reviewer changes because a profile names a
        // different runtime key, and for no other reason.
        foreach (var file in DecisionSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var word in new[] { "Codex", "codex", "gpt-5", "OpenAI", "openai" })
            {
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{name}: {word}");
            }
        }
    }

    [Fact]
    public void The_adapter_lives_at_the_boundary_and_nowhere_else()
    {
        // Matched as the type rather than as the family, exactly as the Claude guard does:
        // HubConfig carries CodexRuntimeConfig, and a configuration section that declares
        // which build is installed is a deployment fact, not a use of the adapter.
        var type = new Regex(@"\bCodexRuntime\b", RegexOptions.CultureInvariant);

        var namers = RepoLayout.ProductionSourceFiles
            .Where(f => type.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The adapter that defines it, and the one composition file that registers it.
        Assert.Equal(["CodexRuntime.cs", "CodexRuntimeFactory.cs"], namers);
    }

    [Fact]
    public void The_build_that_runs_is_the_one_configuration_named()
    {
        var runtime = CodeOnly(Source("CodexRuntime.cs"));

        // Always the configured absolute path, and only that. A bare executable name would
        // resolve on PATH, where a developer machine keeps a different Codex.
        Assert.Contains("_options.ExecutablePath", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("\"codex\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("\"codex.exe\"", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", runtime, StringComparison.Ordinal);

        // Exact, not a floor: the behaviour moved between releases in both directions.
        Assert.Contains("_options.AcceptedVersion, StringComparison.Ordinal", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("Version.Parse", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain(">=", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_here_installs_updates_or_downgrades_a_provider()
    {
        foreach (var file in CodexSources().Concat([Factory()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            // Provisioning a provider is a human decision made once, outside this program. A
            // runtime that could reach a package registry could also change, mid-review, which
            // build produced the finding.
            foreach (var forbidden in new[]
                     {
                         "npm", "npx", "winget", "choco", "curl", "@openai/codex",
                         "HttpClient", "Invoke-WebRequest", "DownloadFile",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_reviewer_runs_read_only_and_says_so_before_launching()
    {
        var invocation = CodeOnly(Source("CodexInvocation.cs"));

        Assert.Equal("read-only", CodexInvocationBuilder.SandboxMode);
        Assert.Equal("never", CodexInvocationBuilder.ApprovalPolicy);

        // A write capable request is refused by name rather than quietly downgraded: an
        // adapter that reviews must not be reachable as one that implements.
        Assert.Contains("AgentPermissionProfile.ReadOnly", invocation, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "workspace-write", "danger-full-access", "--add-dir", "--dangerously" })
        {
            Assert.False(invocation.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [Fact]
    public void The_capabilities_this_adapter_declares_are_the_ones_it_has()
    {
        var capabilities = new CodexRuntime(
            new CodexOptions
            {
                ExecutablePath = @"C:\pinned\codex.exe",
                AcceptedVersion = "0.130.0",
                HealthProbeDirectory = @"C:\pinned",
            },
            new RefusingProcesses()).Describe();

        Assert.True(capabilities.SupportsReadOnlyMode);
        Assert.True(capabilities.SupportsStructuredSchema);

        // The caller cannot state a tool list and have it honoured, so the capability that
        // promises exactly that is not claimed - however much the invocation restricts.
        Assert.False(capabilities.SupportsToolAllowlist);
        Assert.False(capabilities.SupportsSessionResume);
        Assert.False(capabilities.SupportsCostCap);
    }

    [Fact]
    public void No_authentication_architecture_arrived_with_the_second_provider()
    {
        foreach (var file in CodexSources().Concat([Factory()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[] { "ApiKey", "AccessToken", "RefreshToken", "Bearer", "login" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name}: {forbidden}");
            }
        }

        // The one place an OpenAI variable is named is the list of names a child never gets.
        Assert.True(CodexInvocationBuilder.IsForbiddenEnvironmentName("OPENAI_API_KEY"));
        Assert.DoesNotContain("OPENAI_API_KEY", CodexInvocationBuilder.PermittedEnvironmentNames);
    }

    [Fact]
    public void The_user_machine_configuration_is_never_written_to()
    {
        foreach (var file in CodexSources().Concat([Factory()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            // Every bit of hardening is per invocation. Editing a person's own Codex setup to
            // make the Hub's review safer would be the Hub reaching outside its own life.
            foreach (var forbidden in new[] { "config.toml", "auth.json", ".codex", "CODEX_HOME" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }

        // The runtime writes exactly one file: the schema it was given, outside the tree.
        var runtime = CodeOnly(Source("CodexRuntime.cs"));

        Assert.Single(runtime.Split('\n'), l => l.Contains("File.WriteAllText", StringComparison.Ordinal));
        Assert.Contains("Path.GetTempPath()", runtime, StringComparison.Ordinal);
        Assert.Contains("encoderShouldEmitUTF8Identifier: false", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_starts_no_process_of_its_own()
    {
        foreach (var file in CodexSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[] { "Process.Start", "ProcessStartInfo", "cmd.exe", "powershell", "/c " })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }

        Assert.Contains("_processes.RunAsync", CodeOnly(Source("CodexRuntime.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_review_retry_stays_where_it_was_and_does_not_move_into_a_provider()
    {
        var runtime = CodeOnly(Source("CodexRuntime.cs"));

        // One retry, for a document that did not arrive, owned by ReviewEngine and provider
        // neutral. A second copy inside an adapter would make the rule depend on which
        // provider happened to be configured.
        foreach (var forbidden in new[] { "retry", "MaximumAttempts" })
        {
            Assert.False(runtime.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }

        // As the word, not as a substring: the adapter reports how many commands the reviewer
        // attempted, and counting is not retrying.
        Assert.DoesNotMatch(new Regex(@"\battempts?\b", RegexOptions.IgnoreCase), runtime);
    }

    [Fact]
    public void A_structured_result_is_never_invented_or_extracted_from_prose()
    {
        var runtime = CodeOnly(Source("CodexRuntime.cs"));

        // No fence stripping, no fallback shape, no empty object stood in for a missing one.
        foreach (var forbidden in new[] { "```", "IndexOf(\"{\"", "\"{}\"", "JsonSerializer.Deserialize" })
        {
            Assert.False(runtime.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        Assert.Contains("stream.FinalMessage", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_that_made_this_provider_active_touched_no_decision_code()
    {
        // The Phase 8 claim, asserted against history rather than against intent: between the
        // commit that added the adapter and the commit that made it the reviewer, no file
        // under Orchestration, Review or Acceptance changed at all.
        //
        // Written as a shape check rather than a git call, because the guard has to keep
        // holding after those commits are ancient: whatever else happens, these three areas
        // never learn which provider they got.
        foreach (var file in DecisionSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var branch in new[]
                     {
                         "IAgentRuntime runtime", "is CodexRuntime", "is ClaudeCodeRuntime",
                         "GetType()", "RuntimeKey ==", "Provider ==", "nameof(Codex",
                     })
            {
                Assert.False(code.Contains(branch, StringComparison.Ordinal), $"{name}: {branch}");
            }
        }
    }

    [Fact]
    public void Later_phase_work_still_has_not_arrived()
    {
        foreach (var file in CodexSources().Concat([Factory()]))
        {
            var text = File.ReadAllText(file);

            foreach (var forbidden in new[]
                     {
                         "RepairLoop", "RepairEngine", "WebApplication", "ControllerBase", "MapGet(",
                         "BillingAccount", "CredentialStore",
                     })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), forbidden);
            }
        }
    }

    /// <summary>Everything that sequences an execution or decides its fate.</summary>
    private static IEnumerable<string> DecisionSources() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(Path.DirectorySeparatorChar + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && f.Contains(Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        .Concat(Directory.EnumerateFiles(
            Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Review"), "*.cs"))
        .Concat(Directory.EnumerateFiles(
            Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Acceptance"), "*.cs"));

    private static IEnumerable<string> CodexSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Agents", "Codex"), "*.cs");

    private static string Source(string fileName) =>
        File.ReadAllText(Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Agents", "Codex", fileName));

    private static string Factory() =>
        Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Composition", "Agents", "CodexRuntimeFactory.cs");

    private static string CodeOnly(string source) =>
        string.Join(
            '\n',
            source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private sealed class RefusingProcesses : IAgentProcessRunner
    {
        public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a capability declaration must not start anything");
    }
}
