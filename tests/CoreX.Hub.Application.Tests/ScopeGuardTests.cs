using CoreX.Hub.Application.Gates;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// K2. Changed paths against the task's declared scope, with deny winning.
/// </summary>
public sealed class ScopeGuardTests
{
    private static readonly string[] Allowed = ["src/**"];

    private static GateVerdict Evaluate(
        IReadOnlyList<string> paths,
        IReadOnlyList<string>? allowed = null,
        IReadOnlyList<string>? forbidden = null) =>
        ScopeGuard.Evaluate(
            GateFixtures.Changes(changedPaths: paths),
            allowed ?? Allowed,
            forbidden ?? []);

    [Fact]
    public void A_single_path_inside_the_allowed_scope_passes()
    {
        Assert.Equal(GateStatus.Passed, Evaluate(["src/orders/Order.cs"]).Status);
    }

    [Fact]
    public void Several_allowed_paths_pass()
    {
        var verdict = Evaluate(["src/a.cs", "src/deep/nested/b.cs", "src/c.cs"]);

        Assert.Equal(GateStatus.Passed, verdict.Status);
        Assert.Empty(verdict.ScopeEscapes);
    }

    [Fact]
    public void One_path_outside_the_allowed_scope_blocks_the_whole_change()
    {
        var verdict = Evaluate(["src/a.cs", "infra/deploy.yaml"]);

        Assert.True(verdict.Blocked);
        Assert.Contains(ScopeGuard.ScopeViolation, verdict.Reason, StringComparison.Ordinal);

        var escape = Assert.Single(verdict.ScopeEscapes);
        Assert.Equal("infra/deploy.yaml", escape.RelativePath);
        Assert.Contains("no allowed scope", escape.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Forbidden_scope_wins_over_allowed_scope()
    {
        var verdict = Evaluate(
            ["src/orders/Order.cs", "src/generated/Client.cs"],
            allowed: ["src/**"],
            forbidden: ["src/generated/**"]);

        Assert.True(verdict.Blocked);

        var escape = Assert.Single(verdict.ScopeEscapes);
        Assert.Equal("src/generated/Client.cs", escape.RelativePath);
        Assert.Contains("forbidden scope", escape.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_untracked_new_path_is_judged_like_any_other()
    {
        // The capture reports added, modified and deleted paths the same way, so the gate
        // does not need to know which is which - and cannot be evaded by choosing one.
        Assert.True(Evaluate(["docs/new-file.md"]).Blocked);
        Assert.Equal(GateStatus.Passed, Evaluate(["src/new-file.cs"]).Status);
    }

    [Fact]
    public void A_deleted_path_still_counts_as_touched()
    {
        Assert.True(Evaluate(["infra/removed.yaml"]).Blocked);
    }

    [Fact]
    public void A_rename_cannot_hide_an_escape_on_either_side()
    {
        // Both sides of a rename appear in the changed path list, so moving a file out of
        // scope is caught by the destination and moving one in is caught by the source.
        var out_of_scope_destination = Evaluate(["src/Order.cs", "infra/Order.cs"]);
        var out_of_scope_source = Evaluate(["infra/Order.cs", "src/Order.cs"]);

        Assert.True(out_of_scope_destination.Blocked);
        Assert.True(out_of_scope_source.Blocked);
        Assert.Equal("infra/Order.cs", Assert.Single(out_of_scope_destination.ScopeEscapes).RelativePath);
    }

    [Theory]
    [InlineData("../outside.cs", "traverses")]
    [InlineData("src/../../outside.cs", "traverses")]
    [InlineData("/etc/passwd", "rooted")]
    [InlineData(@"\\server\share\file.cs", "UNC")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "drive")]
    [InlineData("   ", "empty")]
    public void A_malformed_path_fails_closed(string path, string expectedReason)
    {
        var verdict = Evaluate([path]);

        Assert.True(verdict.Blocked);
        Assert.Contains(expectedReason, Assert.Single(verdict.ScopeEscapes).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Separator_and_prefix_differences_are_normalised()
    {
        foreach (var spelling in new[] { "src/a.cs", @"src\a.cs", "./src/a.cs", "src//a.cs" })
        {
            Assert.Equal(GateStatus.Passed, Evaluate([spelling]).Status);
        }
    }

    [Fact]
    public void Matching_does_not_depend_on_the_current_directory()
    {
        var before = Directory.GetCurrentDirectory();

        try
        {
            var first = Evaluate(["src/a.cs", "infra/b.yaml"]);

            Directory.SetCurrentDirectory(Path.GetTempPath());

            var second = Evaluate(["src/a.cs", "infra/b.yaml"]);

            Assert.Equal(first.Reason, second.Reason);
            Assert.Equal(
                first.ScopeEscapes.Select(e => e.RelativePath),
                second.ScopeEscapes.Select(e => e.RelativePath));
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }

    [Fact]
    public void Evaluating_scope_touches_no_file()
    {
        using var probe = new TempDirectory();

        var verdict = Evaluate([Path.Combine(probe.Path, "a.cs")]);

        // The path is absolute, so it fails closed - and nothing was created, read or
        // deleted to decide that. Scope is a question about names, not about disk.
        Assert.True(verdict.Blocked);
        Assert.Empty(Directory.EnumerateFileSystemEntries(probe.Path));
    }

    [Fact]
    public void A_scope_glob_matches_across_and_within_segments_predictably()
    {
        Assert.Equal(GateStatus.Passed, Evaluate(["src/a.cs"], allowed: ["src/*.cs"]).Status);
        Assert.True(Evaluate(["src/deep/a.cs"], allowed: ["src/*.cs"]).Blocked);

        Assert.Equal(GateStatus.Passed, Evaluate(["src/deep/a.cs"], allowed: ["src/**"]).Status);
        Assert.Equal(GateStatus.Passed, Evaluate(["src/a.cs"], allowed: ["src/"]).Status);
        Assert.Equal(GateStatus.Passed, Evaluate(["docs/readme.md"], allowed: ["**/*.md"]).Status);
    }
}

/// <summary>A throwaway directory used to prove the gate does not touch the filesystem.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corex-scope-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
