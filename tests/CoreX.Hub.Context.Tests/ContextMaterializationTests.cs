using System.Runtime.Versioning;
using System.Text.Json;
using CoreX.Hub.Context;

namespace CoreX.Hub.Context.Tests;

/// <summary>
/// What lands on disk, where it lands, and what deliberately never gets into it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ContextMaterializationTests
{
    [Fact]
    public async Task The_pack_contains_exactly_the_accepted_file_set()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        Assert.True(result.Succeeded, result.Describe());

        var pack = result.Pack!;

        Assert.Equal(
            [
                "decisions/0001-storage.md",
                "decisions/0002-logging.md",
                "repo-state.json",
                "rules.md",
                "system.md",
                "task.md",
            ],
            pack.Files.Select(f => f.RelativePath));

        // manifest.json exists on disk but is not part of the hashed inventory, which is
        // what keeps the hash free of self reference.
        Assert.True(File.Exists(Path.Combine(pack.PackPath, ContextPackBuilder.ManifestFileName)));
        Assert.DoesNotContain(pack.Files, f => f.RelativePath.Contains("manifest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_manifest_describes_the_pack_deterministically()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        var pack = result.Pack!;

        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(pack.PackPath, ContextPackBuilder.ManifestFileName)));

        var root = manifest.RootElement;

        Assert.Equal(ContextPackBuilder.ManifestFormat, root.GetProperty("Format").GetString());
        Assert.Equal(pack.ContextPackHash, root.GetProperty("ContextPackHash").GetString());
        Assert.Equal(ContextPackBuilder.MaxPackBytes, root.GetProperty("BudgetBytes").GetInt32());
        Assert.Equal(pack.TotalBytes, root.GetProperty("TotalBytes").GetInt64());

        var files = root.GetProperty("Files").EnumerateArray().ToList();
        Assert.Equal(pack.Files.Count, files.Count);

        // Ordered by relative path, not by whatever the filesystem happens to return.
        var paths = files.Select(f => f.GetProperty("Path").GetString()!).ToList();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);

        // Every recorded hash matches the bytes actually on disk.
        foreach (var file in pack.Files)
        {
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(pack.PackPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Equal(file.SizeBytes, bytes.LongLength);
            Assert.Equal(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                file.Sha256);
        }

        // And the published hash really is the function of that inventory.
        Assert.Equal(ContextPackBuilder.ComputePackHash(pack.Files), pack.ContextPackHash);
    }

    [Fact]
    public async Task The_pack_lives_outside_the_workspace()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        var pack = result.Pack!;

        Assert.StartsWith(fixture.ContextRoot, pack.PackPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            pack.PackPath.StartsWith(fixture.WorkspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the context pack was written inside the workspace");
    }

    [Fact]
    public async Task Repository_state_carries_git_facts_and_nothing_else()
    {
        using var fixture = ContextFixture.Create();

        var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
        var json = fixture.Read(result.Pack!, ContextPackBuilder.RepositoryStateFileName);

        Assert.Contains(ContextFixture.BaseCommit, json, StringComparison.Ordinal);
        Assert.Contains("main", json, StringComparison.Ordinal);

        // No credentials, no authentication, no billing, no absolute machine paths and no
        // timestamp: a pack built twice has to be byte identical.
        foreach (var forbidden in new[]
                 {
                     "ApiKey", "apiKey", "token", "password", "secret", "Authorization",
                     "USERPROFILE", "AppData", "Users", "Timestamp", "GeneratedAt",
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task No_pack_file_contains_environment_or_machine_state()
    {
        using var fixture = ContextFixture.Create();

        Environment.SetEnvironmentVariable("COREX_CONTEXT_PROBE_SECRET", "probe-value-must-not-appear");

        try
        {
            var result = await fixture.Builder.BuildAsync(fixture.Request(), CancellationToken.None);
            var pack = result.Pack!;

            foreach (var file in Directory.EnumerateFiles(pack.PackPath, "*", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);

                Assert.DoesNotContain("probe-value-must-not-appear", text, StringComparison.Ordinal);
                Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(fixture.Root, text, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("COREX_CONTEXT_PROBE_SECRET", null);
        }
    }
}
