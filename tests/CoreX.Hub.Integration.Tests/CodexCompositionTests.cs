using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Cli.Agents;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Configuration to a pinned Codex runtime, through the real reader and the real registry.
///
/// The extra refusal this family has is the version. "Some Codex is installed" is not a
/// usable fact here: the behaviour a review depends on moved between releases, and the build
/// a developer machine has on PATH is a different one. So a runtime that does not state which
/// build it accepts is refused at composition, before an execution row could exist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CodexCompositionTests
{
    [Fact]
    public void A_declared_codex_runtime_becomes_one_the_profile_can_reach()
    {
        using var installation = CodexInstallation.Create();

        var runtimes = AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath);

        Assert.True(runtimes.ContainsKey("codex-reviewer-runtime"));

        var runtime = runtimes["codex-reviewer-runtime"];

        // Behind the shared interface and nothing else: composition is the only place that
        // knows which provider this is.
        Assert.IsAssignableFrom<IAgentRuntime>(runtime);

        // And it declares what a reviewer needs, truthfully.
        var capabilities = runtime.Describe();

        Assert.True(capabilities.SupportsReadOnlyMode);
        Assert.True(capabilities.SupportsStructuredSchema);
        Assert.False(capabilities.SupportsSessionResume);

        AgentRuntimeRegistry.EnsureResolvable(runtimes, Profile("codex-reviewer-runtime"));
    }

    [Fact]
    public void A_codex_runtime_that_states_no_accepted_version_is_refused()
    {
        using var installation = CodexInstallation.Create(acceptedVersion: string.Empty);

        var refusal = Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath));

        Assert.Contains("states no accepted version", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("pinned to an exact build", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("codex.exe")]
    public void A_codex_runtime_without_an_absolute_executable_is_refused(string executablePath)
    {
        using var installation = CodexInstallation.Create(executablePath: executablePath);

        // Never discovered on PATH. On this machine PATH holds a newer Codex that the Phase 8
        // gate measured and rejected, so falling back would silently run the review on it.
        Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath));
    }

    [Fact]
    public void A_codex_runtime_whose_executable_is_missing_is_refused_at_composition_time()
    {
        using var installation = CodexInstallation.Create(executablePath: @"C:\nowhere\absent-codex.exe");

        Assert.Contains(
            "does not exist",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                installation.Config, installation.WorkspaceRoot, installation.GitConfigPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_runtimes_of_any_family_cannot_claim_the_same_key()
    {
        using var installation = CodexInstallation.Create(duplicateKey: true);

        Assert.Contains(
            "more than one runtime",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                installation.Config, installation.WorkspaceRoot, installation.GitConfigPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_codex_configuration_type_is_no_place_for_a_secret()
    {
        foreach (var property in typeof(CodexRuntimeConfig).GetProperties())
        {
            foreach (var forbidden in new[]
                     {
                         "ApiKey", "Token", "Secret", "Password", "Credential",
                         "BaseUrl", "Endpoint", "Url", "Account", "Billing", "Subscription",
                     })
            {
                Assert.False(
                    property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"CodexRuntimeConfig.{property.Name} assumes {forbidden}");
            }
        }
    }

    // --- The shipped configuration -------------------------------------------------------

    [Fact]
    public void The_shipped_configuration_declares_a_pinned_codex_and_a_reviewer_that_can_use_it()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(FindRepositoryRoot(), "hub.config.yaml"));

        var declared = Assert.Single(config.Agents.CodexRuntimes);

        Assert.Equal("codex-reviewer-runtime", declared.Key);
        Assert.True(Path.IsPathFullyQualified(declared.ExecutablePath));
        Assert.Equal("0.130.0", declared.AcceptedVersion);

        // The pinned build lives outside the repository. A provider binary is not source.
        Assert.False(
            declared.ExecutablePath.Contains("CoreX Agent Hub", StringComparison.OrdinalIgnoreCase),
            "the pinned provider binary must not live inside the repository");

        var organization = Assert.Single(config.Organizations, o => o.Slug == "core-x-logic");
        var profile = Assert.Single(organization.AgentProfiles, p => p.Name == "codex-reviewer");

        Assert.Equal(AgentRole.Reviewer.ToString(), profile.Role);
        Assert.Equal(AgentPermissionProfile.ReadOnly.ToString(), profile.PermissionProfile);
        Assert.Equal(declared.Key, profile.RuntimeKey);

        // Measured in the gate: the newer default model forces a code-mode and multi-agent
        // surface a reviewer must not have. This one does not.
        Assert.Equal("gpt-5.5", profile.Model);
        Assert.Contains(profile.Effort!, CodexInvocationBuilder.EffortLevels);
    }

    [Fact]
    public void The_active_reviewer_is_still_the_claude_one()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(FindRepositoryRoot(), "hub.config.yaml"));

        var repository = config.Organizations
            .Single(o => o.Slug == "core-x-logic")
            .Projects.Single(p => p.Slug == "agent-hub")
            .Repositories.Single(r => r.Slug == "corex-agent-hub");

        // The Codex profile exists and is not in use. A foundation slice that quietly entered
        // the real review path would make the switch impossible to accept on its own terms.
        Assert.Equal("claude-reviewer", repository.ReviewerProfile);
    }

    private static AgentProfile Profile(string runtimeKey) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "codex-reviewer",
        AgentRole.Reviewer,
        "Codex",
        runtimeKey,
        AgentPermissionProfile.ReadOnly,
        "gpt-5.5",
        "high",
        900,
        1200,
        1800,
        DateTimeOffset.UnixEpoch);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreX.Hub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class CodexInstallation : IDisposable
    {
        private CodexInstallation(string root, HubConfig config)
        {
            Root = root;
            Config = config;
            WorkspaceRoot = Path.Combine(root, "workspace");
            GitConfigPath = Path.Combine(root, "empty.gitconfig");

            Directory.CreateDirectory(WorkspaceRoot);
            File.WriteAllText(GitConfigPath, string.Empty);
        }

        public string Root { get; }

        public string WorkspaceRoot { get; }

        public string GitConfigPath { get; }

        public HubConfig Config { get; }

        public static CodexInstallation Create(
            string? executablePath = null,
            string acceptedVersion = "0.130.0",
            bool duplicateKey = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "corex-codex-composition", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(root);

            var installed = Path.Combine(root, "codex.exe");
            File.WriteAllText(installed, "not a real executable, only a path that exists", new UTF8Encoding(false));

            var declared = new List<CodexRuntimeConfig>
            {
                new()
                {
                    Key = "codex-reviewer-runtime",
                    ExecutablePath = executablePath ?? installed,
                    AcceptedVersion = acceptedVersion,
                },
            };

            if (duplicateKey)
            {
                declared.Add(new CodexRuntimeConfig
                {
                    Key = "codex-reviewer-runtime",
                    ExecutablePath = installed,
                    AcceptedVersion = acceptedVersion,
                });
            }

            return new CodexInstallation(
                root,
                new HubConfig { Agents = new HubAgentsConfig { CodexRuntimes = declared } });
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
}
