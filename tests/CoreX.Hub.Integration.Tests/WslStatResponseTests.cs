using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Telling "the path is not there" apart from "nothing was measured".
///
/// These two produce identical output from a batched <c>stat</c> - an absent row - and the
/// readiness result has to report them completely differently. Reading a launch that never
/// entered the environment as a set of findings would fill a diagnostic with faults an operator
/// cannot reproduce, in a distribution that is fine.
///
/// It cannot be tested through a real environment, because arranging one that is registered and
/// cannot be entered means damaging the operator's machine. So the decision is a pure function
/// and is tested as one.
/// </summary>
public sealed class WslStatResponseTests
{
    [Fact]
    public void A_response_carrying_the_control_row_was_measured()
    {
        string[] lines = ["/proc/version|proc", "/srv/corex/exec|ext2/ext3"];

        Assert.True(WslStatResponse.Reached(lines));
    }

    /// <summary>
    /// The case the whole type exists for.
    ///
    /// A launch that fails outside the environment writes something - wsl.exe reports its own
    /// error - and none of it is an answer about any path that was asked about.
    /// </summary>
    [Fact]
    public void A_response_without_the_control_row_was_not_measured()
    {
        string[] lines = ["The RPC call contains a handle that differs from the declared handle type"];

        Assert.False(WslStatResponse.Reached(lines));
    }

    [Fact]
    public void An_empty_response_was_not_measured()
    {
        Assert.False(WslStatResponse.Reached([]));
    }

    /// <summary>
    /// A measured response can still be missing rows, and those absences are real.
    ///
    /// This is what makes the control row worth the extra path in every call: the same shape -
    /// two of three rows - is a finding here and is nothing at all above.
    /// </summary>
    [Fact]
    public void A_measured_response_reports_the_paths_that_were_absent()
    {
        string[] lines = ["/proc/version|proc", "/srv/corex/exec|ext2/ext3"];

        Assert.True(WslStatResponse.Reached(lines));
        Assert.Equal("ext2/ext3", WslStatResponse.Field(lines, "/srv/corex/exec", index: 1));
        Assert.Null(WslStatResponse.Field(lines, "/srv/corex/evidence", index: 1));
    }

    [Fact]
    public void A_row_is_matched_on_its_path_and_not_on_its_position()
    {
        string[] lines =
        [
            "/proc/version|-r--r--r--|root|root|444",
            "/srv/corex/evidence|drwx------|corex-hub|corex-hub|700",
            "/srv/corex/exec|drwxrwx---|corex-hub|corex-agent|770",
        ];

        Assert.Equal("corex-hub", WslStatResponse.Field(lines, "/srv/corex/evidence", index: 2));
        Assert.Equal("700", WslStatResponse.Field(lines, "/srv/corex/evidence", index: 4));
        Assert.Equal("770", WslStatResponse.Field(lines, "/srv/corex/exec", index: 4));
    }

    /// <summary>A row that stopped short is not silently read as a shorter row.</summary>
    [Fact]
    public void A_row_missing_the_requested_field_answers_nothing()
    {
        string[] lines = ["/proc/version|proc", "/srv/corex/exec"];

        Assert.Null(WslStatResponse.Field(lines, "/srv/corex/exec", index: 1));
    }

    /// <summary>A path that merely contains another is not the same path.</summary>
    [Fact]
    public void A_prefix_is_not_a_match()
    {
        string[] lines = ["/proc/version|proc", "/srv/corex/execution|ext4"];

        Assert.Null(WslStatResponse.Field(lines, "/srv/corex/exec", index: 1));
    }
}
