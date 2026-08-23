using System.Reflection;
using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The shared contract, asserted against the architecture's own wording rather than
/// against whatever the code currently happens to declare.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentContractTests
{
    [Fact]
    public void The_runtime_interface_has_exactly_three_members()
    {
        var members = typeof(IAgentRuntime).GetMembers(BindingFlags.Public | BindingFlags.Instance);

        Assert.Equal(
            ["CheckHealthAsync", "Describe", "RunAsync"],
            members.Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_runtime_interface_grows_no_provider_or_account_member()
    {
        // Adding any of these for one provider would make every other provider implement
        // a lie, so they are named here and forbidden by name.
        string[] forbidden =
        [
            "Authenticate", "GetModels", "GetBilling", "GetQuota", "GetAccount",
            "SetApiKey", "CreateSession", "Commit", "Push",
        ];

        var names = typeof(IAgentRuntime).GetMembers().Select(m => m.Name).ToList();

        foreach (var member in forbidden)
        {
            Assert.DoesNotContain(names, n => n.Contains(member, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Capabilities_declare_exactly_the_frozen_set()
    {
        Assert.Equal(
            [
                "SupportsCostCap", "SupportsReadOnlyMode", "SupportsSessionResume",
                "SupportsStreaming", "SupportsStructuredSchema", "SupportsToolAllowlist",
            ],
            CapabilityProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        Assert.All(CapabilityProperties(), p => Assert.Equal(typeof(bool), p.PropertyType));
    }

    [Fact]
    public void The_run_request_carries_no_provider_access_or_backend_concept()
    {
        string[] forbidden =
        [
            "Provider", "ApiKey", "AccessMode", "Credential", "Subscription",
            "Billing", "Account", "BaseUrl", "Endpoint", "Token", "Auth",
        ];

        foreach (var property in typeof(AgentRunRequest).GetProperties())
        {
            foreach (var word in forbidden)
            {
                Assert.False(
                    property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"AgentRunRequest.{property.Name} drags {word} into the shared contract");
            }
        }

        // What it does carry is the frozen shape.
        foreach (var expected in new[]
                 {
                     "WorkingDirectory", "SystemPromptFile", "AppendSystemPromptFile", "UserPrompt",
                     "PermissionProfile", "ModelHint", "EffortHint", "StructuredOutputSchema",
                     "StructuredOutputPath", "EnvironmentAllowlist", "SoftTimeout", "IdleTimeout",
                     "HardTimeout", "CorrelationId", "SessionId", "ResumeSessionId",
                 })
        {
            Assert.NotNull(typeof(AgentRunRequest).GetProperty(expected));
        }
    }

    [Fact]
    public void The_run_result_reports_the_frozen_outcomes()
    {
        Assert.Equal(
            ["Cancelled", "Completed", "Crashed", "Failed", "TimedOut"],
            Enum.GetNames<AgentRunOutcome>().Order(StringComparer.Ordinal));

        // Evidence and input to orchestration, not a verdict: there is no execution
        // outcome, no gate result and no acceptance decision on this type.
        foreach (var forbidden in new[] { "Accepted", "Passed", "ScopeViolation", "SecretGuard", "Verification" })
        {
            Assert.DoesNotContain(
                typeof(AgentRunResult).GetProperties(),
                p => p.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_scripted_runtime_declares_its_capabilities_truthfully()
    {
        using var fixture = ScriptedFixture.Create();

        var capabilities = fixture.Runtime(ScriptedAgentMode.NoOp).Describe();

        // It supports none of them, and says so. Writing a fixed document to a requested
        // path is not schema support: the runtime never reads StructuredOutputSchema and
        // never checks its output against one, so claiming the capability would let a
        // caller believe a schema had been honoured when nothing looked at it.
        Assert.False(capabilities.SupportsStructuredSchema);
        Assert.False(capabilities.SupportsSessionResume);
        Assert.False(capabilities.SupportsToolAllowlist);
        Assert.False(capabilities.SupportsCostCap);
        Assert.False(capabilities.SupportsStreaming);
        Assert.False(capabilities.SupportsReadOnlyMode);

        // A runtime that claimed a capability it does not have would hide exactly the gaps
        // that Unsupported exists to make visible.
        Assert.All(CapabilityProperties(), p => Assert.False((bool)p.GetValue(capabilities)!));
    }

    [Fact]
    public async Task Health_is_deterministic_and_offline()
    {
        using var fixture = ScriptedFixture.Create();

        var first = await fixture.Runtime(ScriptedAgentMode.NoOp).CheckHealthAsync(CancellationToken.None);
        var second = await fixture.Runtime(ScriptedAgentMode.NoOp).CheckHealthAsync(CancellationToken.None);

        Assert.True(first.IsHealthy);
        Assert.Equal(first.Detail, second.Detail);

        // It checks what this runtime needs and nothing else: no vendor CLI probing.
        foreach (var vendor in new[] { "claude", "codex", "gemini", "qwen" })
        {
            Assert.DoesNotContain(vendor, first.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Health_reports_a_missing_fixture_rather_than_pretending()
    {
        using var fixture = ScriptedFixture.Create();

        var runtime = new ScriptedAgentRuntime(
            new ScriptedAgentOptions
            {
                FixtureExecutablePath = Path.Combine(fixture.Root, "does-not-exist.exe"),
                Mode = ScriptedAgentMode.NoOp,
            },
            fixture.Processes);

        var health = await runtime.CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
        Assert.Contains("does not exist", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agents_module_depends_only_on_the_domain()
    {
        var references = typeof(IAgentRuntime).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("CoreX.Hub.Domain", references);

        foreach (var forbidden in new[]
                 {
                     "CoreX.Hub.Execution", "CoreX.Hub.Workspace", "CoreX.Hub.Verification",
                     "CoreX.Hub.Context", "CoreX.Hub.Application", "CoreX.Hub.Infrastructure", "CoreX.Hub.Cli",
                 })
        {
            Assert.DoesNotContain(references, r => r.StartsWith(forbidden, StringComparison.Ordinal));
        }
    }

    /// <summary>The declared capabilities themselves, without the convenience factory.</summary>
    private static PropertyInfo[] CapabilityProperties() =>
        typeof(AgentCapabilities).GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
