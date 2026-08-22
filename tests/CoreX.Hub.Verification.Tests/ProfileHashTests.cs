using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Tests;

/// <summary>
/// The profile hash is a cache identity, so it must change exactly when the recipe changes
/// and never when something incidental does.
/// </summary>
public sealed class ProfileHashTests
{
    private static VerificationProfile Baseline() => ProfileBuilder.Profile(
        ".",
        ProfileBuilder.Step("build", ["exit", "0"]),
        ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
            parser: VerificationResultParser.JUnit, resultPath: "r.xml"));

    [Fact]
    public void The_same_semantic_profile_hashes_the_same_way()
    {
        var first = ProfileHasher.Compute(Baseline());
        var second = ProfileHasher.Compute(Baseline());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.True(first.All(Uri.IsHexDigit));
    }

    [Fact]
    public void Changing_an_argument_changes_the_hash()
    {
        var changed = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "1"]),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "r.xml"));

        Assert.NotEqual(ProfileHasher.Compute(Baseline()), ProfileHasher.Compute(changed));
    }

    [Fact]
    public void Changing_a_timeout_a_required_flag_a_parser_or_a_result_path_changes_the_hash()
    {
        var original = ProfileHasher.Compute(Baseline());

        var timeout = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"], timeoutSeconds: 31),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "r.xml"));

        var required = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"], required: false),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "r.xml"));

        var parser = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"]),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.Trx, resultPath: "r.xml"));

        var resultPath = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("build", ["exit", "0"]),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "other.xml"));

        Assert.NotEqual(original, ProfileHasher.Compute(timeout));
        Assert.NotEqual(original, ProfileHasher.Compute(required));
        Assert.NotEqual(original, ProfileHasher.Compute(parser));
        Assert.NotEqual(original, ProfileHasher.Compute(resultPath));
    }

    [Fact]
    public void Reordering_the_steps_changes_the_hash()
    {
        var reordered = ProfileBuilder.Profile(
            ".",
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "r.xml"),
            ProfileBuilder.Step("build", ["exit", "0"]));

        Assert.NotEqual(ProfileHasher.Compute(Baseline()), ProfileHasher.Compute(reordered));
    }

    [Fact]
    public void Changing_the_working_directory_changes_the_hash()
    {
        var moved = ProfileBuilder.Profile(
            "src",
            ProfileBuilder.Step("build", ["exit", "0"]),
            ProfileBuilder.Step("test", ["report", "r.xml", "junit-pass"],
                parser: VerificationResultParser.JUnit, resultPath: "r.xml"));

        Assert.NotEqual(ProfileHasher.Compute(Baseline()), ProfileHasher.Compute(moved));
    }

    [Fact]
    public void The_canonical_form_contains_no_incidental_runtime_detail()
    {
        var canonical = ProfileHasher.BuildCanonicalText(Baseline());

        Assert.DoesNotContain(ProfileBuilder.RepositoryId.ToString(), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileBuilder.OrganizationId.ToString(), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-profile", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("1970", canonical, StringComparison.Ordinal);

        Assert.Contains("step=build", canonical, StringComparison.Ordinal);
        Assert.Contains("arg=exit", canonical, StringComparison.Ordinal);
    }
}
