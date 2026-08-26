using System.Text;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The argv encoder, checked against a table rather than against intuition.
///
/// <c>CreateProcessW</c> takes one string, so an argument vector has to survive a round trip
/// through a format whose escaping rules are genuinely surprising: a backslash is literal
/// except when a run of them meets a quote, and then the run is halved. Everything that has
/// ever gone wrong with Windows command lines lives in that sentence, which is why the cases
/// below are written as the exact strings a parser must give back.
///
/// The companion to this is <see cref="NativeLaunchTests"/>, which sends the same shapes
/// through a real process and asks what actually arrived. This file pins the encoding; that
/// one pins the round trip. Neither on its own would be enough.
/// </summary>
public sealed class WindowsCommandLineTests
{
    [Theory]
    // Nothing special: quoting would only add characters a parser has to remove again.
    [InlineData("plain", "plain")]
    [InlineData("C:\\Windows\\System32", "C:\\Windows\\System32")]
    [InlineData("trailing\\", "trailing\\")]
    [InlineData("a\\\\b", "a\\\\b")]

    // An empty argument has no bare representation at all.
    [InlineData("", "\"\"")]

    // Whitespace separates arguments, so it forces quoting.
    [InlineData("two words", "\"two words\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("C:\\Program Files\\app", "\"C:\\Program Files\\app\"")]

    // A trailing backslash inside quotes would escape the closing quote, so the run doubles.
    [InlineData("ends with\\", "\"ends with\\\\\"")]
    [InlineData("ends with\\\\", "\"ends with\\\\\\\\\"")]

    // A quote is escaped; backslashes before it double first, then one escapes the quote.
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]

    // Backslashes not adjacent to a quote stay exactly as they are.
    [InlineData("a\\\\b c", "\"a\\\\b c\"")]
    public void An_argument_encodes_to_the_string_the_parser_will_undo(string argument, string expected)
    {
        var builder = new StringBuilder();

        WindowsCommandLine.AppendArgument(builder, argument);

        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void The_executable_is_repeated_as_the_first_token()
    {
        // CreateProcessW is told the executable separately, but the child reads argv[0] out
        // of the command line. Omitting it would shift every argument by one.
        var line = WindowsCommandLine.Build(@"C:\tools\corex.exe", ["run", "TASK-1"]);

        Assert.Equal(@"C:\tools\corex.exe run TASK-1", line);
    }

    [Fact]
    public void An_executable_path_containing_a_space_is_quoted()
    {
        var line = WindowsCommandLine.Build(@"C:\Program Files\corex\corex.exe", ["go"]);

        Assert.Equal("\"C:\\Program Files\\corex\\corex.exe\" go", line);
    }

    [Fact]
    public void Arguments_are_separated_by_exactly_one_space()
    {
        Assert.Equal("exe a b c", WindowsCommandLine.Build("exe", ["a", "b", "c"]));
    }

    [Fact]
    public void An_argument_vector_of_none_is_just_the_executable()
    {
        Assert.Equal("exe", WindowsCommandLine.Build("exe", []));
    }

    [Fact]
    public void Unicode_survives_untouched()
    {
        // No transliteration and no quoting: these are ordinary characters to the parser.
        var line = WindowsCommandLine.Build("exe", ["görev", "日本語", "emoji-🚀"]);

        Assert.Equal("exe görev 日本語 emoji-🚀", line);
    }

    [Fact]
    public void Shell_operators_are_ordinary_characters()
    {
        // There is no shell (ADR-0006). These need quoting only where they contain spaces,
        // and never acquire meaning.
        var line = WindowsCommandLine.Build("exe", ["a&b", "c|d", "e>f"]);

        Assert.Equal("exe a&b c|d e>f", line);
    }
}
