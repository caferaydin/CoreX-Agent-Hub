using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// A parser enabled step must say where its report lands. Searching a repository for
/// "some .trx" would make verification depend on whatever happened to be on disk.
/// </summary>
public sealed class VerificationStepTests
{
    private static VerificationStep Step(
        VerificationResultParser parser, string? resultPath) =>
        new("test", "dotnet", ["test"], 60, true, parser, resultPath);

    [Fact]
    public void A_parser_step_must_declare_a_result_path()
    {
        var error = Assert.Throws<DomainException>(() => Step(VerificationResultParser.Trx, null));

        Assert.Contains("resultPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_without_a_parser_must_not_declare_a_result_path()
    {
        var error = Assert.Throws<DomainException>(() =>
            Step(VerificationResultParser.None, "results/results.trx"));

        Assert.Contains("resultParser", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_result_path_is_rejected()
    {
        var error = Assert.Throws<DomainException>(() =>
            Step(VerificationResultParser.Trx, @"C:\somewhere\results.trx"));

        Assert.Contains("relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_result_path_is_kept_as_written()
    {
        var step = Step(VerificationResultParser.JUnit, " results/results.xml ");

        Assert.Equal("results/results.xml", step.ResultPath);
        Assert.Equal(VerificationResultParser.JUnit, step.ResultParser);
    }

    [Fact]
    public void Two_steps_differing_only_by_result_path_are_not_equal()
    {
        var first = Step(VerificationResultParser.Trx, "a/results.trx");
        var second = Step(VerificationResultParser.Trx, "b/results.trx");

        // Structural equality drives the idempotent configuration seed, so a changed
        // report location must be seen as a change.
        Assert.NotEqual(first, second);
        Assert.NotEqual(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Two_structurally_identical_steps_stay_equal()
    {
        Assert.Equal(
            Step(VerificationResultParser.Trx, "results/results.trx"),
            Step(VerificationResultParser.Trx, "results/results.trx"));
    }
}
