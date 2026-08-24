using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Cli;
using CoreX.Hub.Cli.Orchestration;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Whether a repository that says it is reviewed can actually be reviewed here.
///
/// Every refusal below happens at composition, before an execution row could exist. Finding
/// any of them at the Review step instead would mean an execution ending as ReviewUnusable
/// because of a mistake in a configuration file - after a worktree, an agent run, a result
/// commit and a full verification had already been spent on it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReviewCompositionTests
{
    [Fact]
    public async Task A_repository_that_names_no_reviewer_composes_and_is_simply_not_reviewed()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var composition = fixture.Compose(ReviewerSelection.None);

        Assert.False(composition.ReviewRequired);
    }

    [Fact]
    public async Task A_reviewer_profile_that_the_organization_does_not_declare_is_refused()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(new ReviewerSelection("claude-reviewer", null)));

        Assert.Contains("claude-reviewer", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not declare", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_with_the_wrong_role_cannot_be_the_reviewer()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(fixture.Reviewer(role: AgentRole.Implementer)));

        Assert.Contains("Implementer", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_that_could_write_is_not_an_independent_reviewer()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(fixture.Reviewer(permission: AgentPermissionProfile.WorkspaceWrite)));

        Assert.Contains("WorkspaceWrite", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "high")]
    [InlineData("claude-opus-5", null)]
    public async Task A_reviewer_with_no_stated_model_or_effort_is_refused(string? model, string? effort)
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(fixture.Reviewer(model: model, effort: effort)));

        // A review is evidence, and evidence produced by an unnamed model at an unnamed
        // effort cannot be compared with the next one.
        Assert.Contains("model and an effort", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_naming_a_runtime_nobody_declared_is_refused()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(fixture.Reviewer(runtimeKey: "a-runtime-nobody-declared")));

        Assert.Contains("a-runtime-nobody-declared", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_declares_no_read_only_mode_cannot_review()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        // ADR-0008: the runtime says what it can do and is believed. Emulating a read only
        // mode a provider does not have would be inventing containment.
        var refusal = Assert.Throws<HubConfigurationException>(
            () => fixture.Compose(fixture.Reviewer(runtimeKey: "scripted")));

        Assert.Contains("read only mode", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_that_can_be_honoured_composes_and_the_repository_is_reviewed()
    {
        await using var fixture = await CompositionFixture.CreateAsync();

        var composition = fixture.Compose(fixture.Reviewer());

        Assert.True(composition.ReviewRequired);

        // The Hub owned reviewer contract is materialised where the Hub owns it, and nowhere
        // an agent or a reviewer could reach.
        var contract = Path.Combine(fixture.DataDirectory, "review", ReviewerPrompt.FileName);

        Assert.True(File.Exists(contract));

        var markdown = await File.ReadAllTextAsync(contract);

        Assert.Contains("Untrusted content", markdown, StringComparison.Ordinal);
        Assert.Contains("Do not modify any file", markdown, StringComparison.Ordinal);

        // Deliberately says nothing about how many findings to produce: a reviewer told to
        // find something will find something, and a threshold calibrated against that number
        // would be measuring the instruction rather than the code.
        Assert.Contains("no minimum and no maximum", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_configuration_names_a_reviewer_the_hub_can_honour()
    {
        var config = new YamlHubConfigReader().Read(
            Path.Combine(FindRepositoryRoot(), "hub.config.yaml"));

        var organization = Assert.Single(config.Organizations, o => o.Slug == "core-x-logic");
        var repository = organization.Projects
            .Single(p => p.Slug == "agent-hub")
            .Repositories.Single(r => r.Slug == "corex-agent-hub");

        Assert.Equal("claude-reviewer", repository.ReviewerProfile);

        var profile = Assert.Single(organization.AgentProfiles, p => p.Name == repository.ReviewerProfile);

        Assert.Equal(AgentRole.Reviewer.ToString(), profile.Role);
        Assert.Equal(AgentPermissionProfile.ReadOnly.ToString(), profile.PermissionProfile);
        Assert.Equal("claude-code", profile.RuntimeKey);
        Assert.False(string.IsNullOrWhiteSpace(profile.Model));
        Assert.False(string.IsNullOrWhiteSpace(profile.Effort));

        // The reviewer and the implementer are different profiles reaching the same declared
        // runtime. Phase 8 points the reviewer elsewhere by editing this profile and nothing
        // else.
        var implementer = Assert.Single(organization.AgentProfiles, p => p.Name == "claude-implementer");

        Assert.Equal(implementer.RuntimeKey, profile.RuntimeKey);
        Assert.NotEqual(implementer.PermissionProfile, profile.PermissionProfile);
    }

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

    /// <summary>
    /// A whole installation, composed exactly as the run command composes one.
    ///
    /// Nothing here is a stand-in for composition: the same <see cref="ExecutionComposition"/>
    /// entry point runs, against a real session, a real configuration file and a real runtime
    /// registry. Only the catalogue rows are built in memory, because seeding them would test
    /// the seeder rather than the refusals.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class CompositionFixture : IAsyncDisposable
    {
        private readonly TempHub _hub;
        private readonly HubSession _session;

        private CompositionFixture(TempHub hub, HubSession session, HubConfig config, string root)
        {
            _hub = hub;
            _session = session;
            Config = config;
            Root = root;

            Organization = new Organization(
                Guid.CreateVersion7(),
                Slug.Parse("hub-dev"),
                "Hub Development Fixture",
                root,
                IsolationLevel.Logical,
                false,
                ["PATH", "SystemRoot"],
                DateTimeOffset.UnixEpoch);

            Repository = new Repository(
                Guid.CreateVersion7(),
                Organization.Id,
                Guid.CreateVersion7(),
                Slug.Parse("hub-sample-repo"),
                "hub-sample",
                Path.Combine(root, "repo"),
                "main",
                ["main"],
                DateTimeOffset.UnixEpoch);
        }

        public HubConfig Config { get; }

        public string Root { get; }

        public string DataDirectory => _hub.DataDirectory;

        public Organization Organization { get; }

        public Repository Repository { get; }

        public static async Task<CompositionFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "corex-review-composition", Guid.CreateVersion7().ToString("N"));

            Directory.CreateDirectory(Path.Combine(root, "repo"));
            Directory.CreateDirectory(Path.Combine(root, "workspace"));

            var fixtureExecutable = Path.Combine(root, "fixture-agent.exe");
            var installedExecutable = Path.Combine(root, "installed-tool.exe");

            File.WriteAllText(fixtureExecutable, "path that exists", new UTF8Encoding(false));
            File.WriteAllText(installedExecutable, "path that exists", new UTF8Encoding(false));

            var hub = TempHub.Create();

            var session = await HubSession.OpenAsync(
                hub.DataDirectory, hub.ConfigPath, TextWriter.Null, CancellationToken.None);

            var config = new HubConfig
            {
                Workspace = new HubWorkspaceConfig { Root = Path.Combine(root, "workspace") },
                Verification = new HubVerificationConfig
                {
                    Executables = { ["dotnet"] = installedExecutable },
                },
                Agents = new HubAgentsConfig
                {
                    ScriptedRuntimes =
                    [
                        new ScriptedRuntimeConfig
                        {
                            Key = "scripted",
                            FixturePath = fixtureExecutable,
                            Mode = "apply-patch",
                        },
                    ],
                    ClaudeCodeRuntimes =
                    [
                        new ClaudeCodeRuntimeConfig { Key = "claude-code", ExecutablePath = installedExecutable },
                    ],
                },
            };

            return new CompositionFixture(hub, session, config, root);
        }

        public ExecutionComposition Compose(ReviewerSelection reviewer) =>
            ExecutionComposition.Create(
                _session, Config, Organization, Repository, null, null, reviewer, TimeProvider.System);

        public ReviewerSelection Reviewer(
            AgentRole role = AgentRole.Reviewer,
            AgentPermissionProfile permission = AgentPermissionProfile.ReadOnly,
            string runtimeKey = "claude-code",
            string? model = "claude-opus-5",
            string? effort = "high") =>
            new(
                "claude-reviewer",
                new AgentProfile(
                    Guid.CreateVersion7(),
                    Organization.Id,
                    "claude-reviewer",
                    role,
                    "ClaudeCode",
                    runtimeKey,
                    permission,
                    model,
                    effort,
                    900,
                    1200,
                    1800,
                    DateTimeOffset.UnixEpoch));

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            _hub.Dispose();

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
