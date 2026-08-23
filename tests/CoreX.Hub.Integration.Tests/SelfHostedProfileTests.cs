using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Provider neutrality includes self hosted models. A locally run harness - vLLM, Ollama,
/// an OpenAI compatible endpoint or a customer's own executable behind it - has to be
/// expressible in the accepted configuration and the accepted 15 table model, today,
/// without a schema change and without a new type.
///
/// This proves representability only. Phase 5 implements no model client of any kind.
/// </summary>
public sealed class SelfHostedProfileTests
{
    /// <summary>
    /// The profile a locally hosted setup would declare. Indentation is taken from the
    /// fixture's own profile so the block lands in the right YAML scope.
    /// </summary>
    private static readonly string[] LocalProfileLines =
    [
        "- name: local-implementer",
        "  role: Implementer",
        "  provider: self-hosted",
        "  runtimeKey: local-agent",
        "  permissionProfile: WorkspaceWrite",
        "  model: qwen3-coder",
        "  softTimeoutSeconds: 300",
        "  idleTimeoutSeconds: 600",
        "  hardTimeoutSeconds: 1800",
    ];

    [Fact]
    public async Task A_self_hosted_agent_profile_seeds_and_reads_back_unchanged()
    {
        using var hub = TempHub.Create(WithLocalProfile());

        await using var db = await hub.NewMigratedContextAsync();
        var config = new YamlHubConfigReader().Read(hub.ConfigPath);
        await new CatalogSeeder(new EfHubStore(db)).SeedAsync(config, DateTimeOffset.UtcNow, CancellationToken.None);

        var profile = await db.AgentProfiles.SingleAsync(p => p.Name == "local-implementer");

        Assert.Equal("self-hosted", profile.Provider);
        Assert.Equal("local-agent", profile.RuntimeKey);
        Assert.Equal("qwen3-coder", profile.Model);

        // Nothing in the catalog asked where the model runs, how it authenticates, or who
        // owns it - and nothing had to change to accept an identifier the Hub has never
        // heard of.
        Assert.NotEqual(Guid.Empty, profile.Id);
    }

    [Fact]
    public async Task Seeding_a_self_hosted_profile_needs_no_schema_change()
    {
        using var hub = TempHub.Create(WithLocalProfile());

        await using var db = await hub.NewMigratedContextAsync();
        var config = new YamlHubConfigReader().Read(hub.ConfigPath);
        await new CatalogSeeder(new EfHubStore(db)).SeedAsync(config, DateTimeOffset.UtcNow, CancellationToken.None);

        // The accepted model still has exactly the accepted tables: representing a locally
        // hosted runtime added none.
        var tables = await db.Database
            .SqlQuery<string>($"SELECT name FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();

        var userTables = tables
            .Where(t => !t.StartsWith("sqlite_", StringComparison.Ordinal)
                        && !t.StartsWith("__EFMigrations", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(15, userTables.Count);
    }

    [Fact]
    public void A_runtime_that_owns_its_own_model_may_leave_the_model_empty()
    {
        // A harness such as a wrapped local coding agent decides the model itself, so the
        // Hub must not force a value it would only be guessing at.
        var yaml = string.Join(
            '\n',
            WithLocalProfile().Split('\n')
                .Where(l => !l.Contains("model: qwen3-coder", StringComparison.Ordinal)));

        using var hub = TempHub.Create(yaml);

        var config = new YamlHubConfigReader().Read(hub.ConfigPath);
        var profile = config.Organizations
            .SelectMany(o => o.AgentProfiles)
            .Single(p => p.Name == "local-implementer");

        Assert.Null(profile.Model);
        Assert.Equal("self-hosted", profile.Provider);
    }

    private static string WithLocalProfile()
    {
        var lines = FixtureConfig.Default.Split('\n').ToList();

        var anchor = lines.FindIndex(l => l.Contains("- name: scripted-implementer", StringComparison.Ordinal));
        Assert.True(anchor >= 0, "the fixture configuration has no scripted agent profile to extend");

        var indent = new string(' ', lines[anchor].Length - lines[anchor].TrimStart().Length);

        var end = lines.FindIndex(anchor, l => l.Contains("hardTimeoutSeconds:", StringComparison.Ordinal));
        Assert.True(end >= 0, "the fixture agent profile has no hardTimeoutSeconds line");

        lines.InsertRange(end + 1, LocalProfileLines.Select(l => indent + l));
        return string.Join('\n', lines);
    }
}
