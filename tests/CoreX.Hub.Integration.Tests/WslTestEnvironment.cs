using System.Globalization;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// Which real WSL distribution, if any, this machine's tests are allowed to run inside.
///
/// The rule is that permission is given, never inferred. These fixtures start transient systemd
/// units, create accounts and directories, and execute workloads as another identity, so pointing
/// one of them at a distribution is a decision with consequences for whatever is in it.
///
/// The previous behaviour inferred it. Each fixture listed the machine's distributions and took the
/// first whose name contained "ubuntu" or "debian", falling back to whichever came first - so on an
/// ordinary developer laptop a plain <c>dotnet test</c> selected the operator's own working Ubuntu
/// and ran a guest workload in it. Nothing about a distribution's name is consent, and the name of
/// a distro family says nothing at all about whether it is disposable.
///
/// So there is one environment variable, the operator sets it, and there is no fallback of any
/// kind. Absent, every real test skips and says what to set. Present but malformed, or naming a
/// distribution that is not registered, the tests fail rather than skip: someone asked for a real
/// run and did not get one, and quietly reporting nothing wrong would hide the typo.
/// </summary>
internal static class WslTestEnvironment
{
    /// <summary>
    /// The one variable. Deliberately not one per fixture.
    ///
    /// Every fixture that reads it wants the same thing - a single disposable distribution to run
    /// inside - so several names would be several ways to say it and one more of them to forget.
    /// The paired acceptance harnesses keep their own variables because they want something
    /// different: two environments at once, to measure one against the other.
    /// </summary>
    public const string Variable = "COREX_HARD_TEST_ENV";

    /// <summary>
    /// The environment the operator named, or nothing.
    ///
    /// Nothing means nothing. There is no default distribution, no first registered distribution
    /// and no preferred family - the absence of an answer is the answer.
    /// </summary>
    public static string? Selected() => SelectedFrom(Environment.GetEnvironmentVariable(Variable));

    /// <summary>
    /// The same decision, over a value the caller supplies.
    ///
    /// Exists so the rule can be tested without touching the real variable. Process environment is
    /// shared by every test running at the same time, and xUnit runs classes in parallel - a test
    /// that set this one to prove a point would be telling every fixture initialising alongside it
    /// that an operator had opted in. That is not a hypothetical: a test proving "Ubuntu is never
    /// selected implicitly" would, for as long as it held the variable, be an explicit instruction
    /// to run inside the developer's Ubuntu.
    /// </summary>
    public static string? SelectedFrom(string? value)
    {
        if (value is null || value.Trim().Length == 0)
        {
            return null;
        }

        if (!TryValidate(value, out var name, out var problem))
        {
            Assert.Fail($"{Variable} is set to something unusable: {problem}");
        }

        return name;
    }

    /// <summary>
    /// Whether a supplied name can be handed to <c>wsl.exe</c> as one argument and mean only that.
    ///
    /// The value becomes an element of a real argument vector. There is no shell in the way, so
    /// quoting is not the risk; what is left is a value that stops being a name - one that reads as
    /// an option, or that carries a control character into an argument, a log line or a readiness
    /// detail. Both are refused rather than sanitised, because a name that had to be repaired is
    /// not the name the operator meant.
    /// </summary>
    public static bool TryValidate(string? value, out string name, out string problem)
    {
        name = string.Empty;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "it is empty.";
            return false;
        }

        // Compared before trimming: surrounding whitespace in a distribution name is a typo that
        // would otherwise be silently accepted as a different name than the one on the machine.
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            problem = "it begins or ends with whitespace.";
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                problem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"it contains the control character U+{(int)character:X4}.");

                return false;
            }
        }

        if (value.StartsWith('-') || value.StartsWith('/'))
        {
            problem = "it starts like an option rather than a name.";
            return false;
        }

        name = value;
        return true;
    }

    /// <summary>
    /// Confirms the named environment is really registered on this machine.
    ///
    /// Asked so that a typo fails as a typo. Without it a misspelled name would reach every command
    /// in the fixture, each would fail on its own terms, and the run would look like a broken
    /// boundary rather than a misspelled variable.
    /// </summary>
    public static bool IsRegistered(IReadOnlyList<string> registered, string name) =>
        registered.Any(r => string.Equals(r.Trim(), name, StringComparison.Ordinal));
}
