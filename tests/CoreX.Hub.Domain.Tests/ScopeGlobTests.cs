using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// The one authority for what a task scope entry may be, canonically is, and matches.
/// </summary>
public sealed class ScopeGlobTests
{
    [Theory]
    [InlineData("src/**")]
    [InlineData("src/*.cs")]
    [InlineData("src/?/x")]
    [InlineData("src/")]
    [InlineData("docs/**/*.md")]
    [InlineData("README.md")]
    public void A_repository_relative_pattern_is_accepted(string glob)
    {
        Assert.True(ScopeGlob.TryParse(glob, out var parsed, out var error), error);
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("../x", "traverses")]
    [InlineData("src/../../x", "traverses")]
    [InlineData("/root", "rooted")]
    [InlineData(@"\root", "rooted")]
    [InlineData(@"C:\x", "drive")]
    [InlineData(@"\\server\share", "UNC")]
    [InlineData("//server/share", "UNC")]
    public void A_pattern_that_escapes_the_repository_is_refused(string glob, string expectedReason)
    {
        Assert.False(ScopeGlob.TryParse(glob, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains(expectedReason, error!, StringComparison.OrdinalIgnoreCase);

        var thrown = Assert.Throws<DomainException>(() => ScopeGlob.Parse(glob));
        Assert.Contains(expectedReason, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"src\**", "src/**")]
    [InlineData("./src/**", "src/**")]
    [InlineData("src//**", "src/**")]
    [InlineData("  src/**  ", "src/**")]
    public void Separator_and_prefix_differences_canonicalise_to_one_form(string written, string canonical)
    {
        Assert.Equal(canonical, ScopeGlob.Parse(written).Value);
    }

    [Fact]
    public void Equivalent_patterns_compare_equal()
    {
        var first = ScopeGlob.Parse(@"src\orders\**");
        var second = ScopeGlob.Parse("src/orders/**");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, ScopeGlob.Parse("src/other/**"));
    }

    [Fact]
    public void A_single_star_stays_within_one_segment()
    {
        var glob = ScopeGlob.Parse("src/*.cs");

        Assert.True(glob.Matches("src/Order.cs"));
        Assert.False(glob.Matches("src/deep/Order.cs"));
        Assert.False(glob.Matches("Order.cs"));
    }

    [Fact]
    public void A_question_mark_matches_one_character_within_a_segment()
    {
        var glob = ScopeGlob.Parse("src/?/x.cs");

        Assert.True(glob.Matches("src/a/x.cs"));
        Assert.False(glob.Matches("src/ab/x.cs"));
        Assert.False(glob.Matches("src//x.cs"));
    }

    [Fact]
    public void A_double_star_crosses_segments_and_may_match_none()
    {
        var glob = ScopeGlob.Parse("src/**");

        Assert.True(glob.Matches("src/Order.cs"));
        Assert.True(glob.Matches("src/deep/nested/Order.cs"));
        Assert.False(glob.Matches("infra/Order.cs"));

        var between = ScopeGlob.Parse("src/**/x.cs");
        Assert.True(between.Matches("src/x.cs"));
        Assert.True(between.Matches("src/a/b/x.cs"));
    }

    [Fact]
    public void A_trailing_slash_means_everything_underneath()
    {
        var glob = ScopeGlob.Parse("src/");

        Assert.True(glob.Matches("src/Order.cs"));
        Assert.True(glob.Matches("src/deep/Order.cs"));
        Assert.False(glob.Matches("srcx/Order.cs"));
    }

    [Fact]
    public void The_path_being_matched_is_canonicalised_the_same_way()
    {
        var glob = ScopeGlob.Parse("src/**");

        foreach (var spelling in new[] { "src/a.cs", @"src\a.cs", "./src/a.cs", "src//a.cs" })
        {
            Assert.True(glob.Matches(spelling), spelling);
        }
    }

    [Fact]
    public void Matching_does_not_depend_on_the_current_directory()
    {
        var before = Directory.GetCurrentDirectory();
        var glob = ScopeGlob.Parse("src/**");

        try
        {
            Assert.True(glob.Matches("src/a.cs"));

            Directory.SetCurrentDirectory(Path.GetTempPath());

            Assert.True(glob.Matches("src/a.cs"));
            Assert.False(glob.Matches("infra/a.cs"));
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }
}
