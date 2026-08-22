using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// argv is not a shell command string.
///
/// Characters that would be operators in cmd.exe or a POSIX shell must arrive at the
/// child exactly as written, because ProcessRunner passes an argument vector and never
/// assembles a command line. Nothing destructive is executed: the fixture simply echoes
/// what it received.
/// </summary>
public sealed class ArgumentLiteralTests
{
    [Theory]
    [InlineData("hello & whoami")]
    [InlineData("a | b")]
    [InlineData("write > file.txt")]
    [InlineData("read < file.txt")]
    [InlineData("caret ^ escape")]
    [InlineData("first ; second")]
    [InlineData("sub $(whoami)")]
    [InlineData("back `whoami`")]
    [InlineData("percent %PATH%")]
    [InlineData("quote \" inside")]
    [InlineData("and && or || pipe |")]
    public async Task Shell_metacharacters_arrive_as_literal_argument_text(string argument)
    {
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "echo-args", argument),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);

        var observed = ReadArguments(result.NormalizedStdout);

        Assert.Single(observed);
        Assert.Equal(argument, observed[0]);
    }

    [Fact]
    public async Task Every_argument_keeps_its_own_boundary()
    {
        using var workspace = TempWorkspace.Create();

        string[] arguments =
        [
            "hello & whoami",
            "second | argument",
            "third > redirect",
            "  spaced  ",
            string.Empty,
            "son: İstanbul & doğrulama",
        ];

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), ["echo-args", .. arguments]),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);

        var observed = ReadArguments(result.NormalizedStdout);

        Assert.Equal(arguments.Length, observed.Count);
        Assert.Equal(arguments, observed);

        EvidenceLog.Write("ArgvLiteral input : " + string.Join(" || ", arguments));
        EvidenceLog.Write("ArgvLiteral child : " + string.Join(" || ", observed));
    }

    /// <summary>
    /// The metacharacters never reach an interpreter, so nothing they would normally do
    /// can happen: the child observes text, not a command.
    /// </summary>
    [Fact]
    public async Task A_metacharacter_argument_does_not_produce_extra_output()
    {
        using var workspace = TempWorkspace.Create();

        var result = await workspace.Runner.RunAsync(
            Fixture.Request(workspace, TimeSpan.FromSeconds(30), "echo-args", "hello & whoami"),
            CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Empty(result.NormalizedStderr);

        var lines = result.NormalizedStdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        // Exactly one echoed argument plus the sentinel: no shell ran anything extra.
        Assert.Equal(2, lines.Count);
        Assert.Equal("ARG[0]=hello & whoami", lines[0]);
        Assert.Equal("ARGS-END", lines[1]);
    }

    private static List<string> ReadArguments(string stdout)
    {
        var lines = stdout
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        var end = lines.IndexOf("ARGS-END");
        Assert.True(end >= 0, "fixture did not report ARGS-END");

        var observed = new List<string>();
        for (var i = 0; i < end; i++)
        {
            var line = lines[i];
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            Assert.True(separator > 0, $"unexpected line '{line}'");
            observed.Add(line[(separator + 1)..]);
        }

        return observed;
    }
}
