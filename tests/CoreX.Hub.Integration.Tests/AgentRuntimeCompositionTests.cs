using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.ClaudeCode;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Composition.Agents;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Configuration to runtime, through the real reader and the real registry.
///
/// The point is the whole chain: a profile names a runtime key, configuration says what
/// stands behind that key on this machine, and either the two meet or the installation
/// refuses to start an execution. Every refusal here happens before an execution row could
/// exist, because a configuration mistake is not worth a worktree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentRuntimeCompositionTests
{
    [Fact]
    public void A_declared_runtime_becomes_a_runtime_the_profile_can_reach()
    {
        using var installation = Installation.Create();

        var runtimes = AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath);

        Assert.True(runtimes.ContainsKey("claude-code"));

        var runtime = runtimes["claude-code"];

        // Behind the shared interface and nothing else: composition is the only place that
        // knows what this is.
        Assert.IsAssignableFrom<IAgentRuntime>(runtime);

        AgentRuntimeRegistry.EnsureResolvable(runtimes, Profile("claude-code"));
    }

    [Fact]
    public void A_profile_naming_a_runtime_nobody_declared_is_refused_rather_than_substituted()
    {
        using var installation = Installation.Create();

        var runtimes = AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath);

        var refusal = Assert.Throws<HubConfigurationException>(
            () => AgentRuntimeRegistry.EnsureResolvable(runtimes, Profile("a-runtime-nobody-declared")));

        Assert.Contains("a-runtime-nobody-declared", refusal.Message, StringComparison.Ordinal);

        // Not quietly answered with whatever else happened to be registered.
        Assert.DoesNotContain("scripted", refusal.Message.Split("declared:")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_whose_executable_is_missing_is_refused_at_composition_time()
    {
        using var installation = Installation.Create(executablePath: @"C:\nowhere\absent-tool.exe");

        var refusal = Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath));

        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_declared_without_an_absolute_executable_is_refused()
    {
        using var relative = Installation.Create(executablePath: "tool.exe");

        Assert.Contains(
            "absolute",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                relative.Config, relative.WorkspaceRoot, relative.GitConfigPath)).Message,
            StringComparison.Ordinal);

        using var empty = Installation.Create(executablePath: string.Empty);

        Assert.Contains(
            "no executable path",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                empty.Config, empty.WorkspaceRoot, empty.GitConfigPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_declared_without_a_key_is_refused()
    {
        using var installation = Installation.Create(key: string.Empty);

        Assert.Contains(
            "without a key",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                installation.Config, installation.WorkspaceRoot, installation.GitConfigPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_runtimes_cannot_claim_the_same_key()
    {
        // Silently letting the second win would make which runtime ran depend on file order.
        using var installation = Installation.Create(duplicateKey: true);

        Assert.Contains(
            "more than one runtime",
            Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
                installation.Config, installation.WorkspaceRoot, installation.GitConfigPath)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_fallback_to_a_deterministic_runtime_when_a_real_one_is_missing()
    {
        // A real runtime that cannot be reached must never quietly become the offline harness:
        // the run would appear to succeed and would have done nothing.
        using var installation = Installation.Create(executablePath: @"C:\nowhere\absent-tool.exe");

        Assert.Throws<HubConfigurationException>(() => AgentRuntimeRegistry.Build(
            installation.Config, installation.WorkspaceRoot, installation.GitConfigPath));
    }

    [Fact]
    public void The_configuration_type_is_no_place_for_a_secret_or_an_endpoint()
    {
        foreach (var property in typeof(Application.Configuration.ClaudeCodeRuntimeConfig).GetProperties())
        {
            foreach (var forbidden in new[]
                     {
                         "ApiKey", "Token", "Secret", "Password", "Credential",
                         "BaseUrl", "Endpoint", "Url", "Account", "Billing", "Subscription",
                     })
            {
                Assert.False(
                    property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"ClaudeCodeRuntimeConfig.{property.Name} assumes {forbidden}");
            }
        }
    }

    [Fact]
    public void The_shipped_configuration_declares_the_whole_chain_the_gate_asked_for()
    {
        // G6-3 closes on the chain existing, not on an inventory of what could be written.
        var repositoryRoot = FindRepositoryRoot();
        var config = new YamlHubConfigReader().Read(Path.Combine(repositoryRoot, "hub.config.yaml"));

        var declared = Assert.Single(config.Agents.ClaudeCodeRuntimes);
        Assert.Equal("claude-code", declared.Key);
        Assert.True(Path.IsPathFullyQualified(declared.ExecutablePath));

        var organization = Assert.Single(config.Organizations, o => o.Slug == "core-x-logic");
        Assert.Equal(IsolationLevel.Logical.ToString(), organization.IsolationLevel);
        Assert.False(organization.RequiresHardIsolation);

        var profile = Assert.Single(organization.AgentProfiles, p => p.Name == "claude-implementer");
        Assert.Equal(AgentRole.Implementer.ToString(), profile.Role);
        Assert.Equal("claude-code", profile.RuntimeKey);
        Assert.Equal("claude-opus-5", profile.Model);
        Assert.Equal("high", profile.Effort);
        Assert.Equal(AgentPermissionProfile.WorkspaceWrite.ToString(), profile.PermissionProfile);

        // The model and effort the catalog states have to be ones the adapter can honour
        // exactly, or every run of this profile would be refused.
        Assert.Contains(profile.Effort!, ClaudeCodeInvocationBuilder.EffortLevels);

        // And the environment the organization grants has to cover what the runtime needs.
        foreach (var required in ClaudeCodeInvocationBuilder.RequiredEnvironmentNames)
        {
            Assert.Contains(required, organization.EnvironmentAllowlist);
        }

        foreach (var granted in organization.EnvironmentAllowlist)
        {
            Assert.False(
                ClaudeCodeInvocationBuilder.IsForbiddenEnvironmentName(granted),
                $"the organization grants '{granted}', which a runtime must never inherit");
        }

        var project = Assert.Single(organization.Projects, p => p.Slug == "agent-hub");
        var repository = Assert.Single(project.Repositories, r => r.Slug == "corex-agent-hub");

        Assert.Equal("main", repository.DefaultBranch);
        Assert.Equal(["main"], repository.ProtectedBranches);

        // An absolute path to a directory named for the repository - deliberately not
        // "wherever this test happens to be running". Those are the same thing in a
        // developer checkout and are not during verification, which runs the suite from a
        // disposable snapshot of the commit being measured. Asserting they matched made the
        // shipped configuration look broken every time it was actually being verified.
        Assert.True(Path.IsPathFullyQualified(repository.LocalPath));
        Assert.Equal("Core X Agent Hub", new DirectoryInfo(repository.LocalPath.TrimEnd('\\')).Name);

        // The organization root has to contain the repository, or nothing can be prepared.
        Assert.StartsWith(organization.FilesystemRoot, repository.LocalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_release_verification_profile_proves_a_warning_free_build_mechanically()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(FindRepositoryRoot(), "hub.config.yaml"));

        var repository = config.Organizations
            .Single(o => o.Slug == "core-x-logic")
            .Projects.Single(p => p.Slug == "agent-hub")
            .Repositories.Single(r => r.Slug == "corex-agent-hub");

        var profile = Assert.Single(repository.VerificationProfiles, p => p.Name == "hub-release-full");

        Assert.True(profile.IsDefault);
        Assert.Equal(["restore", "build", "test", "ef-pending-model"], profile.Steps.Select(s => s.Name));
        Assert.All(profile.Steps, s => Assert.True(s.Required));

        var build = profile.Steps.Single(s => s.Name == "build");

        // Exit code zero is not proof of a warning free build. Only this is.
        Assert.Contains("-warnaserror", build.Args);
        Assert.Contains("-c", build.Args);
        Assert.Contains("Release", build.Args);
        Assert.Contains("--no-restore", build.Args);

        // The test step claims no counters, and that is deliberate rather than an omission.
        // `dotnet test` over a solution runs one host per project and every one of them
        // writes the same trx path, so the file that survives holds a single project's
        // results. Parsing it would publish "the suite passed, 117 tests" as machine
        // evidence about a suite of more than a thousand. The exit code is authoritative for
        // the whole solution, so the status is real and the count is simply not claimed.
        var test = profile.Steps.Single(s => s.Name == "test");

        Assert.Equal("none", test.ResultParser);
        Assert.Null(test.ResultPath);
        Assert.DoesNotContain(test.Args, a => a.Contains("trx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("CoreX.Hub.slnx", test.Args);

        // Every step names a logical executable the allowlist declares; PATH is never trusted.
        foreach (var step in profile.Steps)
        {
            Assert.True(
                config.Verification.Executables.ContainsKey(step.Executable),
                $"step '{step.Name}' names executable '{step.Executable}', which is not registered");

            Assert.True(Path.IsPathFullyQualified(config.Verification.Executables[step.Executable]));
        }

        Assert.Equal("dotnet-ef", profile.Steps.Single(s => s.Name == "ef-pending-model").Executable);
    }

    private static AgentProfile Profile(string runtimeKey) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "claude-implementer",
            AgentRole.Implementer,
            "ClaudeCode",
            runtimeKey,
            AgentPermissionProfile.WorkspaceWrite,
            "claude-opus-5",
            "high",
            1800,
            2400,
            3600,
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

        throw new InvalidOperationException("could not locate the repository root");
    }

    /// <summary>A temporary installation with a real executable to point a runtime at.</summary>
    private sealed class Installation : IDisposable
    {
        private Installation(string root, Application.Configuration.HubConfig config)
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

        public Application.Configuration.HubConfig Config { get; }

        public static Installation Create(
            string key = "claude-code",
            string? executablePath = null,
            bool duplicateKey = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "corex-runtime-tests", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(root);

            var installed = Path.Combine(root, "installed-tool.exe");
            File.WriteAllText(installed, "not a real executable, only a path that exists", new UTF8Encoding(false));

            var declared = new List<Application.Configuration.ClaudeCodeRuntimeConfig>
            {
                new() { Key = key, ExecutablePath = executablePath ?? installed },
            };

            if (duplicateKey)
            {
                declared.Add(new Application.Configuration.ClaudeCodeRuntimeConfig
                {
                    Key = key,
                    ExecutablePath = installed,
                });
            }

            return new Installation(
                root,
                new Application.Configuration.HubConfig
                {
                    Agents = new Application.Configuration.HubAgentsConfig { ClaudeCodeRuntimes = declared },
                });
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
