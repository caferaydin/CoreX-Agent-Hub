using System.Runtime.Versioning;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The rule that decides which real WSL distribution the tests may run inside.
///
/// Worth its own tests because it is the safety property rather than a convenience: everything
/// else in this assembly that touches an operating system depends on this answering "nothing"
/// when nobody has said otherwise. It used to answer with the operator's own Ubuntu.
///
/// Every test here runs on any machine and starts no distribution.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WslTestEnvironmentTests
{
    // -- no opt-in -------------------------------------------------------------------------------

    /// <summary>
    /// With nothing set, nothing is selected. There is no second place to look.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Without_an_explicit_environment_nothing_is_selected(string? value) =>
        Assert.Null(WslTestEnvironment.SelectedFrom(value));

    // -- the fallbacks that used to exist --------------------------------------------------------

    /// <summary>
    /// A machine full of ordinary distributions selects none of them.
    ///
    /// This is the defect, written as a test. The old algorithm listed the machine, preferred the
    /// first name containing "ubuntu" or "debian", and fell back to whichever came first - so a
    /// plain <c>dotnet test</c> on a developer laptop picked the developer's Ubuntu and executed a
    /// workload in it. None of these names is permission now, and neither is being first.
    /// </summary>
    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("Ubuntu-24.04")]
    [InlineData("ubuntu")]
    [InlineData("Debian")]
    [InlineData("debian")]
    [InlineData("docker-desktop")]
    public void A_registered_distribution_is_never_selected_by_its_name(string registered)
    {
        // Nothing is selected, whatever the machine happens to have registered.
        Assert.Null(WslTestEnvironment.SelectedFrom(null));

        // And the name being present in a listing is not what makes it selectable either: the only
        // thing that selects it is somebody naming exactly it.
        Assert.True(WslTestEnvironment.IsRegistered([registered], registered));

        Assert.Equal(registered, WslTestEnvironment.SelectedFrom(registered));
    }

    /// <summary>
    /// Being the default distribution, or the only one, is not permission either.
    ///
    /// <c>wsl --list --quiet</c> puts the default first and marks it elsewhere; an algorithm that
    /// took the head of that list would be reading an operator's convenience setting as consent.
    /// </summary>
    [Fact]
    public void Being_first_or_default_does_not_select_a_distribution()
    {
        Assert.Null(WslTestEnvironment.SelectedFrom(null));

        // A listing is only ever used to confirm what was named, never to choose.
        string[] listing = ["Ubuntu", "docker-desktop", "corex-disposable"];

        Assert.True(WslTestEnvironment.IsRegistered(listing, "corex-disposable"));
        Assert.False(WslTestEnvironment.IsRegistered(listing, "corex-not-there"));
    }

    // -- explicit selection ----------------------------------------------------------------------

    /// <summary>An explicitly named environment is selected exactly, and unchanged.</summary>
    [Theory]
    [InlineData("corex-disposable")]
    [InlineData("corexfs-a")]
    [InlineData("Test_Env.1")]
    public void An_explicitly_named_environment_is_selected_exactly(string named) =>
        Assert.Equal(named, WslTestEnvironment.SelectedFrom(named));

    /// <summary>
    /// A name that is not registered is not quietly replaced by one that is.
    ///
    /// The fixtures turn this into a failure rather than a skip, because somebody asked for a real
    /// run and did not get one; a silent substitution would run the whole suite against a
    /// distribution nobody named.
    /// </summary>
    [Fact]
    public void An_unregistered_name_matches_nothing()
    {
        string[] listing = ["Ubuntu", "Debian", "docker-desktop"];

        Assert.False(WslTestEnvironment.IsRegistered(listing, "corex-typo"));

        // Not even a name that is a prefix, a suffix or a case variation of a real one.
        Assert.False(WslTestEnvironment.IsRegistered(listing, "Ubun"));
        Assert.False(WslTestEnvironment.IsRegistered(listing, "Ubuntu-24.04"));
        Assert.False(WslTestEnvironment.IsRegistered(listing, "ubuntu"));
    }

    // -- validation ------------------------------------------------------------------------------

    /// <summary>
    /// A supplied value has to be a name, not argument structure.
    ///
    /// It becomes one element of a real <c>wsl.exe</c> argument vector. There is no shell in the
    /// way, so quoting is not the danger; a value that reads as an option, or that carries a
    /// control character into an argument or a log line, is.
    /// </summary>
    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("-d", "option")]
    [InlineData("--shutdown", "option")]
    [InlineData("/mnt", "option")]
    [InlineData("corex\ndisposable", "control")]
    [InlineData("corex\rdisposable", "control")]
    [InlineData("corex\tdisposable", "control")]
    [InlineData("corex\0disposable", "control")]
    [InlineData(" corex-disposable", "whitespace")]
    [InlineData("corex-disposable ", "whitespace")]
    public void A_value_that_is_not_a_name_is_refused(string value, string because)
    {
        Assert.False(WslTestEnvironment.TryValidate(value, out var name, out var problem));

        Assert.Empty(name);

        // The refusal says which rule it broke, so an operator can fix the variable rather than
        // guess at it.
        Assert.NotEmpty(problem);
        _ = because;
    }

    /// <summary>An ordinary distribution name passes, and is not rewritten on the way through.</summary>
    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("Ubuntu-24.04")]
    [InlineData("corex-disposable")]
    [InlineData("corexfs-a")]
    [InlineData("Test_Env.1")]
    public void An_ordinary_name_is_accepted_unchanged(string value)
    {
        Assert.True(WslTestEnvironment.TryValidate(value, out var name, out var problem));

        Assert.Equal(value, name);
        Assert.Empty(problem);
    }

    /// <summary>
    /// A malformed value fails rather than being treated as absent.
    ///
    /// The difference matters: absent means nobody asked for a real run, and malformed means
    /// somebody did and got it wrong. Reading the second as the first would turn a typo into a
    /// suite that quietly tested nothing.
    /// </summary>
    [Fact]
    public void A_malformed_value_is_not_read_as_no_opt_in()
    {
        var failure = Record.Exception(() => WslTestEnvironment.SelectedFrom("-d"));

        Assert.NotNull(failure);
        Assert.Contains(WslTestEnvironment.Variable, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing here touches the real variable, and that is a safety property rather than tidiness.
    ///
    /// Process environment is shared by every test running at the same moment, and xUnit runs test
    /// classes in parallel. A test that set this variable to prove a rule would, for as long as it
    /// held it, be telling every fixture starting alongside it that an operator had opted in - and
    /// one of the values these tests care about is the name of the developer's own distribution.
    ///
    /// It was found as intermittent failures in unrelated WSL-backed suites, roughly one run in
    /// three, with a different victim each time. That is exactly how it would have gone unnoticed,
    /// and why the rule is now guarded rather than remembered.
    /// </summary>
    [Fact]
    public void These_tests_never_touch_the_real_variable() =>
        Assert.DoesNotContain(
            // Split so this guard does not match its own text.
            "Set" + "EnvironmentVariable",
            File.ReadAllText(
                Path.Combine(
                    RepoRoot(), "tests", "CoreX.Hub.Integration.Tests", "WslTestEnvironmentTests.cs")),
            StringComparison.Ordinal);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CoreX.Hub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
