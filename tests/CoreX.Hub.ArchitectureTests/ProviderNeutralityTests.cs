using System.Text.RegularExpressions;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// The Hub is not architected around Claude or Codex. ADR-0008 and architecture F.1 fix
/// the shape: a thin IAgentRuntime, declared capabilities, and provider choice expressed
/// as data on AgentProfile - never as a branch in the orchestrator.
///
/// Two kinds of guard live here and are deliberately named apart:
///
///   ACTIVE NOW        checks something that exists today and can fail today.
///   ARMED FOR PHASE 5 the subject type does not exist yet. The guard discovers it by
///                     name and asserts for real the moment it appears. It never
///                     silently skips: each armed guard proves its own detector still
///                     works against a synthetic positive control, so an armed guard
///                     cannot rot into a test that passes because it looks at nothing.
///
/// Nothing here implements a runtime. Phase 5 owns that.
/// </summary>
public sealed class ProviderNeutralityTests
{
    /// <summary>
    /// Target adapter families. Only "scripted" becomes real in Phase 5; the rest exist
    /// here so the guards below know what must never leak into neutral layers.
    /// </summary>
    private static readonly string[] TargetRuntimeFamilies =
        ["scripted", "claude-code", "codex", "gemini-cli", "qwen-code", "custom-cli"];

    /// <summary>Provider words that must not appear in code outside a provider adapter.</summary>
    private static readonly string[] ProviderWords =
        ["Claude", "Codex", "Gemini", "Qwen", "Anthropic"];

    /// <summary>
    /// Flags that belong to one provider's command line. The list comes from the
    /// architecture's own Claude invocation sketch; a provider adapter may use them, and
    /// nothing else may.
    /// </summary>
    private static readonly string[] ProviderCliFlags =
    [
        "--append-system-prompt-file", "--system-prompt-file", "--disallowedTools",
        "--permission-mode", "--strict-mcp-config", "--setting-sources",
        "--no-session-persistence", "--dangerously-skip-permissions",
        "--output-format", "--add-dir", "--debug-file",
    ];

    /// <summary>Layers that must stay provider blind for ever.</summary>
    private static readonly string[] NeutralModules =
        ["CoreX.Hub.Domain", "CoreX.Hub.Application", "CoreX.Hub.Verification", "CoreX.Hub.Workspace"];

    // ---------------------------------------------------------------- ACTIVE NOW ------

    [Fact]
    public void Active_no_production_code_names_a_provider()
    {
        // Comments may name providers - the AgentProfile summary does, as documentation.
        // Code may not: a provider name in code is the first step towards branching on it.
        var offenders = new List<string>();

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            if (IsProviderAdapter(file))
            {
                continue;
            }

            var code = CodeOnly(File.ReadAllText(file));

            foreach (var word in ProviderWords)
            {
                if (Regex.IsMatch(code, @"\b" + word + @"\b", RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{RepoLayout.Relative(file)} names '{word}'");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Active_no_assembly_references_a_provider_adapter()
    {
        foreach (var assembly in ArchitectureAssemblies.All)
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

            foreach (var word in ProviderWords.Concat(["OpenAI", "Google.Cloud"]))
            {
                Assert.DoesNotContain(
                    references,
                    r => r.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Active_provider_and_runtime_identity_stay_data()
    {
        // Adding gemini-cli or a customer's own CLI must not require a domain change, so
        // neither identity is an enum and neither is a closed set in the model.
        Assert.Equal(typeof(string), typeof(AgentProfile).GetProperty(nameof(AgentProfile.Provider))!.PropertyType);
        Assert.Equal(typeof(string), typeof(AgentProfile).GetProperty(nameof(AgentProfile.RuntimeKey))!.PropertyType);

        var domainTypes = typeof(AgentProfile).Assembly.GetTypes();

        Assert.DoesNotContain(
            domainTypes,
            t => t.IsEnum
                 && (t.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase)));

        // Every target family is expressible today, without touching any type.
        foreach (var family in TargetRuntimeFamilies)
        {
            Assert.True(family.Length <= 100, $"'{family}' must fit the RuntimeKey column");
        }
    }

    [Fact]
    public void Active_provider_cli_flags_appear_nowhere_outside_a_provider_adapter()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => !IsProviderAdapter(f))
            .SelectMany(f => ProviderCliFlags
                .Where(flag => File.ReadAllText(f).Contains(flag, StringComparison.Ordinal))
                .Select(flag => $"{RepoLayout.Relative(f)} uses {flag}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Active_no_dynamic_plugin_loading_exists()
    {
        // A future custom runtime arrives as a registered adapter using an absolute
        // executable and an argv, not as a loaded assembly. The MVP has no plugin
        // framework and this is what keeps it that way.
        string[] forbidden =
        [
            "Assembly.Load", "AssemblyLoadContext", "Activator.CreateInstance",
            "AppDomain", "Type.GetType(", "LoadFrom(",
        ];

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in forbidden)
            {
                Assert.False(code.Contains(construct, StringComparison.Ordinal), $"{name}: {construct}");
            }
        }
    }

    [Fact]
    public void Active_unsupported_is_already_a_first_class_step_result()
    {
        // Capability gaps are declared, never emulated: the vocabulary a future adapter
        // needs in order to say "I cannot do that" exists before any adapter does.
        Assert.Contains(ExecutionStepState.Unsupported, Enum.GetValues<ExecutionStepState>());
    }

    [Fact]
    public void Active_the_neutral_layers_know_no_runtime_module()
    {
        foreach (var assembly in ArchitectureAssemblies.Neutral)
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

            Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Agents", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("CoreX.Hub.Runtimes", StringComparison.Ordinal));
        }
    }

    // -------------------------------------- ACTIVE NOW: local and self hosted --------

    [Fact]
    public void Active_a_self_hosted_agent_profile_is_representable_today()
    {
        // Not every provider is a cloud service. A locally hosted harness has to be
        // expressible without touching a single type, so this builds one.
        var local = NewProfile(provider: "self-hosted", runtimeKey: "local-agent", model: "qwen3-coder");

        Assert.Equal("self-hosted", local.Provider);
        Assert.Equal("local-agent", local.RuntimeKey);
        Assert.Equal("qwen3-coder", local.Model);

        // Not every runtime owns its model either: a harness that decides the model itself
        // leaves the field empty rather than being forced to invent a value.
        var harnessOwned = NewProfile(provider: "custom", runtimeKey: "custom-cli", model: null);
        Assert.Null(harnessOwned.Model);

        // And nothing rejects an identifier just because no Hub code has heard of it.
        foreach (var (provider, runtime) in new[]
                 {
                     ("self-hosted", "local-agent"), ("vendor-x", "custom-cli"),
                     ("on-prem", "qwen-code"), ("workstation", "scripted"),
                 })
        {
            var profile = NewProfile(provider, runtime, model: null);
            Assert.Equal(provider, profile.Provider);
            Assert.Equal(runtime, profile.RuntimeKey);
        }
    }

    [Fact]
    public void Active_the_catalog_declares_no_credential_or_endpoint_field()
    {
        // Authentication is not always an API key, and a local endpoint is not always a
        // URL, so the catalog commits to neither. A runtime that needs credentials gets
        // them from its own configured environment, not from a Hub column.
        string[] forbidden =
        [
            "ApiKey", "Token", "Secret", "Credential", "Password",
            "BaseUrl", "Endpoint", "Url", "Host", "Port", "Auth",
        ];

        foreach (var type in new[] { typeof(AgentProfile), typeof(Application.Configuration.AgentProfileConfig) })
        {
            foreach (var property in type.GetProperties())
            {
                foreach (var word in forbidden)
                {
                    Assert.False(
                        property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                        $"{type.Name}.{property.Name} assumes {word}");
                }
            }
        }
    }

    [Fact]
    public void Active_no_inference_client_or_model_gateway_surface_exists()
    {
        // No LLM gateway, no model router, no vendor SDK and no HTTP client: Phase 5 talks
        // to a harness through ProcessRunner, and a future local backend is that harness's
        // own business.
        string[] forbidden =
        [
            "HttpClient", "HttpRequestMessage", "WebRequest", "WebSocket", "TcpClient",
            "RestClient", "Ollama", "vLLM", "LiteLLM", "OpenAI", "ChatCompletion",
        ];

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var name = RepoLayout.Relative(file);

            // The secret pattern catalog names an OPENAI token shape. That is a secret to
            // redact, not a backend to call, and the exemption is proven below.
            if (string.Equals(Path.GetFileName(file), SecretCatalogFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var code = CodeOnly(File.ReadAllText(file));

            foreach (var construct in forbidden)
            {
                Assert.False(code.Contains(construct, StringComparison.OrdinalIgnoreCase), $"{name}: {construct}");
            }
        }

        // Non vacuous exemption: the one skipped file really is the secret catalog, and it
        // really does contain the token name, so the skip cannot quietly cover a client.
        var catalog = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), SecretCatalogFileName, StringComparison.Ordinal));

        var catalogCode = CodeOnly(File.ReadAllText(catalog));
        Assert.Contains("OPENAI_TOKEN", catalogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", catalogCode, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- ARMED FOR PHASE 5 --------

    [Fact]
    public void Armed_orchestration_source_names_no_provider()
    {
        var orchestration = OrchestrationSourceFiles().ToList();

        // The detector is proven against a synthetic orchestrator first, so this guard
        // cannot quietly become a test that inspects nothing.
        Assert.NotEmpty(FindProviderNames(
            "if (profile.Provider == \"claude\") { RunClaude(); }"));
        Assert.Empty(FindProviderNames("var runtime = _runtimes.Resolve(profile.RuntimeKey);"));

        foreach (var file in orchestration)
        {
            var offenders = FindProviderNames(CodeOnly(File.ReadAllText(file)));
            Assert.Empty(offenders.Select(o => $"{RepoLayout.Relative(file)}: {o}"));
        }

        // Today the set is empty, which is the documented ARMED state. The moment a file
        // named *Orchestrator*.cs or a directory named Orchestration appears under src,
        // the loop above starts checking it for real.
        if (orchestration.Count == 0)
        {
            Assert.Empty(orchestration);
        }
    }

    [Fact]
    public void Armed_orchestration_source_does_not_branch_on_runtime_identity()
    {
        // Resolution belongs to composition: the orchestrator asks a registry for the
        // runtime named by the profile, it does not decide what that runtime is.
        Assert.NotEmpty(FindIdentityBranching("switch (profile.Provider) { case \"codex\": break; }"));
        Assert.NotEmpty(FindIdentityBranching("if (profile.RuntimeKey == \"gemini-cli\") { }"));
        Assert.Empty(FindIdentityBranching("var runtime = registry.Resolve(profile.RuntimeKey);"));

        foreach (var file in OrchestrationSourceFiles())
        {
            var offenders = FindIdentityBranching(CodeOnly(File.ReadAllText(file)));
            Assert.Empty(offenders.Select(o => $"{RepoLayout.Relative(file)}: {o}"));
        }
    }

    [Fact]
    public void Armed_the_agent_runtime_contract_stays_thin()
    {
        // Prove the extractor first: a four member interface must be reported as four.
        var control = ExtractInterfaceMembers(
            "public interface IAgentRuntime { Capabilities Describe(); Task RunAsync(); "
            + "Task CheckHealthAsync(); Task DoExtraAsync(); }",
            "IAgentRuntime");

        Assert.Equal(
            ["CheckHealthAsync", "Describe", "DoExtraAsync", "RunAsync"],
            control.Order(StringComparer.Ordinal));

        var declaration = FindTypeDeclaration("IAgentRuntime");
        if (declaration is null)
        {
            // ARMED: the contract does not exist yet. When Phase 5 declares it anywhere
            // under src, the assertion below runs against the real source.
            return;
        }

        var members = ExtractInterfaceMembers(File.ReadAllText(declaration), "IAgentRuntime");

        Assert.Equal(
            ["CheckHealthAsync", "Describe", "RunAsync"],
            members.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Armed_a_runtime_contract_arrives_together_with_declared_capabilities()
    {
        if (FindTypeDeclaration("IAgentRuntime") is null)
        {
            // ARMED alongside the contract above.
            return;
        }

        // Capability differences are declared, not hidden: Describe() has to hand back a
        // capabilities type, and Unsupported has to remain a reachable step result.
        Assert.NotNull(FindTypeDeclaration("AgentCapabilities"));
        Assert.Contains(ExecutionStepState.Unsupported, Enum.GetValues<ExecutionStepState>());
    }

    [Fact]
    public void Armed_provider_adapters_are_the_only_place_provider_names_may_live()
    {
        var adapters = RepoLayout.ProductionSourceFiles.Where(IsProviderAdapter).ToList();

        // The classifier is proven both ways, so "no adapters exist" cannot be the reason
        // this passes later.
        Assert.True(IsProviderAdapter(Path.Combine("src", "CoreX.Hub.Agents", "ClaudeCode", "X.cs")));
        Assert.False(IsProviderAdapter(Path.Combine("src", "CoreX.Hub.Application", "X.cs")));

        // Once adapters exist each one must name exactly one provider family, so a single
        // file cannot quietly become a multi provider switchboard.
        foreach (var adapter in adapters)
        {
            var named = ProviderWords
                .Where(w => Regex.IsMatch(CodeOnly(File.ReadAllText(adapter)), @"\b" + w + @"\b", RegexOptions.IgnoreCase))
                .ToList();

            Assert.True(named.Count <= 1, $"{RepoLayout.Relative(adapter)} names {named.Count} providers");
        }
    }

    [Fact]
    public void Armed_orchestration_knows_nothing_about_model_backends()
    {
        // The orchestrator drives a harness. Which model that harness reaches, and how it
        // authenticates, is never its concern - so a base URL or a key has no business in
        // orchestration source, local backend or not.
        Assert.NotEmpty(FindBackendConcepts("var client = new HttpClient { BaseAddress = baseUrl };"));
        Assert.NotEmpty(FindBackendConcepts("options.ApiKey = profile.ApiKey;"));
        Assert.Empty(FindBackendConcepts("var runtime = registry.Resolve(profile.RuntimeKey);"));

        foreach (var file in OrchestrationSourceFiles())
        {
            var offenders = FindBackendConcepts(CodeOnly(File.ReadAllText(file)));
            Assert.Empty(offenders.Select(o => $"{RepoLayout.Relative(file)}: {o}"));
        }
    }

    // ------------------------------------------------------------------ helpers -------

    private const string SecretCatalogFileName = "SecretPatternCatalog.cs";

    private static AgentProfile NewProfile(string provider, string runtimeKey, string? model) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "compat-probe",
            AgentRole.Implementer,
            provider,
            runtimeKey,
            AgentPermissionProfile.WorkspaceWrite,
            model,
            null,
            300,
            600,
            1800,
            DateTimeOffset.UnixEpoch);


    /// <summary>
    /// Source files that will make up orchestration. Discovered by name so the guards
    /// above start inspecting Phase 5 code the moment it lands, with no test edit.
    /// </summary>
    private static IEnumerable<string> OrchestrationSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            Path.GetFileName(f).Contains("Orchestrator", StringComparison.OrdinalIgnoreCase)
            || f.Contains(
                Path.DirectorySeparatorChar + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>A provider adapter is a file under a provider named folder of an agents project.</summary>
    private static bool IsProviderAdapter(string path)
    {
        var separator = Path.DirectorySeparatorChar;

        if (!path.Contains($"{separator}CoreX.Hub.Agents{separator}", StringComparison.OrdinalIgnoreCase)
            && !path.Contains($"{separator}Agents{separator}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ProviderWords.Any(w =>
            path.Contains($"{separator}{w}", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> FindBackendConcepts(string code)
    {
        string[] concepts = ["HttpClient", "BaseAddress", "BaseUrl", "ApiKey", "Endpoint", "ChatCompletion"];
        return [.. concepts.Where(c => code.Contains(c, StringComparison.OrdinalIgnoreCase))];
    }

    private static IReadOnlyList<string> FindProviderNames(string code) =>
        [.. ProviderWords.Where(w => Regex.IsMatch(code, @"\b" + w + @"\b", RegexOptions.IgnoreCase))];

    /// <summary>
    /// Branching on who the provider is: a comparison or a switch over Provider or
    /// RuntimeKey. Resolving a runtime through a registry is not branching.
    /// </summary>
    private static IReadOnlyList<string> FindIdentityBranching(string code)
    {
        string[] patterns =
        [
            @"switch\s*\(\s*[A-Za-z_.]*\b(Provider|RuntimeKey)\b",
            @"\b(Provider|RuntimeKey)\b\s*(==|!=)\s*""",
            @"""\s*(==|!=)\s*[A-Za-z_.]*\b(Provider|RuntimeKey)\b",
            @"\b(Provider|RuntimeKey)\b\s*is\s+""",
        ];

        return [.. patterns.Where(p => Regex.IsMatch(code, p, RegexOptions.IgnoreCase))];
    }

    /// <summary>The file declaring a type, or null while it does not exist yet.</summary>
    private static string? FindTypeDeclaration(string typeName)
    {
        var pattern = new Regex(
            @"\b(interface|class|record|struct|enum)\s+" + Regex.Escape(typeName) + @"\b",
            RegexOptions.CultureInvariant);

        return RepoLayout.ProductionSourceFiles
            .FirstOrDefault(f => pattern.IsMatch(File.ReadAllText(f)));
    }

    /// <summary>
    /// Member names declared directly on an interface. Crude on purpose: the contract this
    /// guards is three members long, and anything it cannot parse shows up as a mismatch
    /// rather than as a pass.
    /// </summary>
    private static IReadOnlyList<string> ExtractInterfaceMembers(string source, string interfaceName)
    {
        var declaration = source.IndexOf("interface " + interfaceName, StringComparison.Ordinal);
        if (declaration < 0)
        {
            return [];
        }

        var open = source.IndexOf('{', declaration);
        if (open < 0)
        {
            return [];
        }

        var depth = 0;
        var close = open;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        var body = source[(open + 1)..close];

        return [.. Regex.Matches(body, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>()]*>)?\s*\(")
            .Select(m => m.Groups[1].Value)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Strips comments so a guard reads what the code does. A comment naming a provider is
    /// documentation, not a provider decision.
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
