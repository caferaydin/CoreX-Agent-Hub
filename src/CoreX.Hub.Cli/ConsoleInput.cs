namespace CoreX.Hub.Cli;

/// <summary>
/// Where a line typed by a person comes from.
///
/// Deliberately one method. The point is not to abstract the terminal, it is that the
/// acceptance checklist has to be answerable by a test without a real console attached -
/// otherwise the only way to exercise a human decision would be by hand, which is the one
/// path that most needs covering.
///
/// Returns null at end of input, which is how a caller distinguishes "nothing more will be
/// typed" from "an empty line was typed".
/// </summary>
public interface IConsoleInput
{
    string? ReadLine();
}

/// <summary>The real terminal.</summary>
public sealed class ConsoleInput : IConsoleInput
{
    public static ConsoleInput Instance { get; } = new();

    public string? ReadLine() => Console.ReadLine();
}

/// <summary>
/// A fixed script of answers, for callers that already know what the person would say -
/// tests, and a non interactive invocation that was given its verdicts on the command line.
/// </summary>
public sealed class ScriptedConsoleInput : IConsoleInput
{
    private readonly Queue<string> _lines;

    public ScriptedConsoleInput(IEnumerable<string> lines)
    {
        _lines = new Queue<string>(lines ?? []);
    }

    public string? ReadLine() => _lines.Count > 0 ? _lines.Dequeue() : null;
}
