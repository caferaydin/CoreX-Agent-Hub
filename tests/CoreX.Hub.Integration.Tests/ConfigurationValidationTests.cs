using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Configuration parsing must be fail closed. An unknown or misspelled YAML key is a
/// configuration error, never a silent fallback to a default CLR value.
///
/// Configuration carries security and execution sensitive fields, so a typo that
/// silently disabled requiresHardIsolation would defeat the ADR-0017 gate before the
/// domain invariant ever saw the intended value.
/// </summary>
public sealed class ConfigurationValidationTests
{
    [Fact]
    public void Valid_configuration_still_loads()
    {
        using var hub = TempHub.Create();

        var config = new YamlHubConfigReader().Read(hub.ConfigPath);

        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(2, config.Organizations.Count);
        Assert.Equal(@"D:\cx", config.Workspace.Root);
    }

    [Fact]
    public void The_shipped_hub_config_still_loads()
    {
        // Guards against the strict parser rejecting the repository's own configuration.
        var config = new YamlHubConfigReader().Read(Path.Combine(RepoRoot(), "hub.config.yaml"));

        Assert.NotEmpty(config.Organizations);
        Assert.Contains(config.Organizations, o => o.Slug == "hub-dev");
    }

    [Fact]
    public void Unknown_top_level_property_is_rejected()
    {
        var error = AssertRejected("schemaVersion:", "unexpectedRootSetting: true");

        Assert.Contains("unexpectedRootSetting", error.Message, StringComparison.Ordinal);
        Assert.Contains("HubConfig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_organization_property_is_rejected()
    {
        var error = AssertRejected("requiresHardIsolation: false", "unexpectedOrgSetting: hello");

        Assert.Contains("unexpectedOrgSetting", error.Message, StringComparison.Ordinal);
        Assert.Contains("OrganizationConfig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_repository_property_is_rejected()
    {
        var error = AssertRejected("defaultBranch: main", "remoteUrl: origin");

        Assert.Contains("remoteUrl", error.Message, StringComparison.Ordinal);
        Assert.Contains("RepositoryConfig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_verification_step_property_is_rejected()
    {
        var error = AssertRejected("resultParser: trx", "retryCount: 3");

        Assert.Contains("retryCount", error.Message, StringComparison.Ordinal);
        Assert.Contains("VerificationStepConfig", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_diagnostic_names_the_file_and_the_position()
    {
        var error = AssertRejected("schemaVersion:", "unexpectedRootSetting: true");

        Assert.Contains("Invalid configuration", error.Message, StringComparison.Ordinal);
        Assert.Contains("hub.config.yaml", error.Message, StringComparison.Ordinal);
        Assert.Contains("line", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-0017 regression. A typo of RequiresHardIsolation must not degrade into
    /// "false", and therefore must never produce an apparently Active organization.
    /// </summary>
    [Fact]
    public async Task A_typo_of_RequiresHardIsolation_is_rejected_rather_than_defaulting_to_false()
    {
        const string Typo = "requiresHardIsolaton";

        var yaml = FixtureConfig.Default.Replace(
            "requiresHardIsolation: true",
            Typo + ": true",
            StringComparison.Ordinal);

        Assert.Contains(Typo, yaml, StringComparison.Ordinal);

        using var hub = TempHub.Create(yaml);

        var error = Assert.Throws<HubConfigurationException>(() => new YamlHubConfigReader().Read(hub.ConfigPath));
        Assert.Contains(Typo, error.Message, StringComparison.Ordinal);

        // The gate organization must not exist at all: rejection happens before any seed.
        await using var db = await hub.NewMigratedContextAsync();
        Assert.Empty(await db.Organizations.ToListAsync());

        // And the CLI refuses to run rather than presenting an Active organization.
        var result = await hub.RunAsync("org", "list");
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Active", result.StdOut, StringComparison.Ordinal);
    }

    private static HubConfigurationException AssertRejected(string anchorLine, string unknownKey)
    {
        using var hub = TempHub.Create(WithUnknownKey(anchorLine, unknownKey));

        return Assert.Throws<HubConfigurationException>(() => new YamlHubConfigReader().Read(hub.ConfigPath));
    }

    /// <summary>
    /// Inserts an unknown key immediately after the first line containing
    /// <paramref name="anchorLine"/>, reusing that line's own indentation so the injected
    /// key lands in the intended YAML scope.
    /// </summary>
    internal static string WithUnknownKey(string anchorLine, string unknownKey)
    {
        var lines = FixtureConfig.Default.Split('\n').ToList();
        var index = lines.FindIndex(l => l.Contains(anchorLine, StringComparison.Ordinal));
        Assert.True(index >= 0, $"Fixture configuration has no line containing '{anchorLine}'.");

        var indent = new string(' ', lines[index].Length - lines[index].TrimStart().Length);
        lines.Insert(index + 1, indent + unknownKey);

        return string.Join('\n', lines);
    }

    private static string RepoRoot()
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
}
