using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Duplicate YAML mapping keys must be rejected.
///
/// YAML would otherwise let the last occurrence silently shadow the first, so the
/// effective configuration would depend on document order rather than on what the
/// operator meant. That is the same configuration-boundary failure mode as an unknown
/// key: security sensitive values such as requiresHardIsolation or a repository
/// localPath could be shadowed without any signal.
/// </summary>
public sealed class ConfigurationDuplicateKeyTests
{
    [Fact]
    public void Duplicate_root_key_is_rejected()
    {
        var schemaVersion = AssertRejected(AppendedAtRoot("schemaVersion: 2"));
        Assert.Contains("duplicate key", schemaVersion.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schemaVersion", schemaVersion.Message, StringComparison.Ordinal);

        var organizations = AssertRejected(AppendedAtRoot("organizations: []"));
        Assert.Contains("duplicate key", organizations.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("organizations", organizations.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_organization_property_is_rejected()
    {
        var error = AssertRejected(
            WithDuplicateKey("requiresHardIsolation: false", "requiresHardIsolation: true"));

        Assert.Contains("duplicate key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requiresHardIsolation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_repository_property_is_rejected()
    {
        var error = AssertRejected(WithDuplicateKey("localPath:", @"localPath: D:\other"));

        Assert.Contains("duplicate key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_verification_step_property_is_rejected()
    {
        var error = AssertRejected(WithDuplicateKey("executable: dotnet", "executable: cmd"));

        Assert.Contains("duplicate key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("executable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_duplicate_diagnostic_names_the_file_and_the_position()
    {
        var error = AssertRejected(WithDuplicateKey("defaultBranch: main", "defaultBranch: master"));

        Assert.Contains("Invalid configuration", error.Message, StringComparison.Ordinal);
        Assert.Contains("hub.config.yaml", error.Message, StringComparison.Ordinal);
        Assert.Contains("line", error.Message, StringComparison.Ordinal);
        Assert.Contains("defaultBranch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_configuration_without_duplicates_still_loads()
    {
        using var hub = TempHub.Create();

        var config = new YamlHubConfigReader().Read(hub.ConfigPath);

        Assert.Equal(2, config.Organizations.Count);
    }

    /// <summary>
    /// ADR-0017 regression. A duplicated requiresHardIsolation must not resolve to the
    /// last value and produce an Active organization; the configuration is rejected
    /// before any effective organization can be seeded.
    /// </summary>
    [Fact]
    public async Task Duplicated_RequiresHardIsolation_is_rejected_rather_than_resolving_to_the_last_value()
    {
        // true (intended) shadowed by false (accidental) - the dangerous direction.
        var yaml = WithDuplicateKey("requiresHardIsolation: true", "requiresHardIsolation: false");

        // Sanity: the gate organization now declares the key twice in the same mapping,
        // the intended true immediately shadowed by an accidental false.
        var yamlLines = yaml.Split('\n').Select(l => l.Trim()).ToList();
        var shadowed = yamlLines.Zip(yamlLines.Skip(1)).Any(pair =>
            pair.First == "requiresHardIsolation: true"
            && pair.Second == "requiresHardIsolation: false");

        Assert.True(shadowed, "Fixture should contain a true/false duplicate pair.");

        using var hub = TempHub.Create(yaml);

        var error = Assert.Throws<HubConfigurationException>(() => new YamlHubConfigReader().Read(hub.ConfigPath));
        Assert.Contains("duplicate key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requiresHardIsolation", error.Message, StringComparison.Ordinal);

        // Nothing was seeded, so there is no organization to be Active in the first place.
        await using var db = await hub.NewMigratedContextAsync();
        Assert.Empty(await db.Organizations.ToListAsync());

        var result = await hub.RunAsync("org", "list");
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Active", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cli_refuses_to_run_with_a_duplicate_key_configuration()
    {
        using var hub = TempHub.Create(WithDuplicateKey("localPath:", @"localPath: D:\other"));

        var result = await hub.RunAsync("org", "list");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("configuration error", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("localPath", result.StdErr, StringComparison.Ordinal);

        // Readable diagnostic, not an uncontrolled stack trace.
        Assert.DoesNotContain("   at ", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("YamlDotNet.Core", result.StdErr, StringComparison.Ordinal);

        // No normal catalog result.
        Assert.DoesNotContain("hub-dev", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("SLUG", result.StdOut, StringComparison.Ordinal);
    }

    private static HubConfigurationException AssertRejected(string yaml)
    {
        using var hub = TempHub.Create(yaml);

        return Assert.Throws<HubConfigurationException>(() => new YamlHubConfigReader().Read(hub.ConfigPath));
    }

    private static string AppendedAtRoot(string rootLine) =>
        FixtureConfig.Default + "\n" + rootLine + "\n";

    /// <summary>
    /// Duplicates a key by inserting <paramref name="duplicateLine"/> immediately after
    /// the first line containing <paramref name="anchorLine"/>, reusing that line's own
    /// indentation so both keys live in the same mapping.
    /// </summary>
    internal static string WithDuplicateKey(string anchorLine, string duplicateLine)
    {
        var lines = FixtureConfig.Default.Split('\n').ToList();
        var index = lines.FindIndex(l => l.Contains(anchorLine, StringComparison.Ordinal));
        Assert.True(index >= 0, $"Fixture configuration has no line containing '{anchorLine}'.");

        var indent = new string(' ', lines[index].Length - lines[index].TrimStart().Length);
        lines.Insert(index + 1, indent + duplicateLine);

        return string.Join('\n', lines);
    }
}
