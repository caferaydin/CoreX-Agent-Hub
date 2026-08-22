using CoreX.Hub.Cli.Verification;
using CoreX.Hub.Infrastructure.Configuration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The verification executable allowlist is configuration, not catalog: it is read from
/// hub.config.yaml, it is never seeded into the database, and it is fail closed.
/// </summary>
public sealed class VerificationConfigurationTests
{
    [Fact]
    public void The_shipped_configuration_declares_the_verification_executable_map()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(RepoRoot(), "hub.config.yaml"));

        Assert.True(config.Verification.Executables.ContainsKey("dotnet"));
        Assert.True(Path.IsPathRooted(config.Verification.Executables["dotnet"]));
    }

    [Fact]
    public void The_configured_map_becomes_the_fail_closed_allowlist()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(RepoRoot(), "hub.config.yaml"));

        var allowlist = VerificationComposition.CreateAllowlist(config, out var problems);

        Assert.Empty(problems);
        Assert.Contains("dotnet", allowlist.LogicalNames);

        // Nothing else is reachable, however common the tool.
        foreach (var name in new[] { "npm", "node", "python", "cmd", "powershell", "git" })
        {
            Assert.False(allowlist.Resolve(name).IsAllowed, $"'{name}' must not resolve");
        }
    }

    [Fact]
    public void A_verification_step_declaring_a_parser_also_declares_where_the_report_lands()
    {
        var config = new YamlHubConfigReader().Read(Path.Combine(RepoRoot(), "hub.config.yaml"));

        var parserSteps = config.Organizations
            .SelectMany(o => o.Projects)
            .SelectMany(p => p.Repositories)
            .SelectMany(r => r.VerificationProfiles)
            .SelectMany(v => v.Steps)
            .Where(s => !string.Equals(s.ResultParser, "none", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(parserSteps);

        foreach (var step in parserSteps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.ResultPath), $"step '{step.Name}' has no resultPath");
            Assert.False(Path.IsPathRooted(step.ResultPath!), $"step '{step.Name}' resultPath must be relative");
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "hub.config.yaml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("hub.config.yaml was not found above the test output.");
    }
}
