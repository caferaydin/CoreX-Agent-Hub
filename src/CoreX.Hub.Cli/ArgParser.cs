namespace CoreX.Hub.Cli;

/// <summary>
/// Minimal argument parser.
///
/// Architecture section N recommends System.CommandLine; that package is still a
/// pre-release with a moving API surface, and the Phase 1 command set is six commands.
/// Section 10 of the Phase 1 brief asks for plain .NET and minimal dependencies, so the
/// parser is hand written. Revisit if the command surface grows materially.
/// </summary>
public sealed class ArgParser
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private ArgParser()
    {
    }

    public IReadOnlyList<string> Positional => _positional;

    public static ArgParser Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parser = new ArgParser();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parser._positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;

            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }

            if (!parser._options.TryGetValue(name, out var list))
            {
                list = [];
                parser._options[name] = list;
            }

            list.Add(value ?? string.Empty);
        }

        return parser;
    }

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string? GetOption(string name) =>
        _options.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;

    public string RequireOption(string name) =>
        GetOption(name) is { Length: > 0 } value
            ? value
            : throw new UsageException($"Missing required option --{name}.");

    public IReadOnlyList<string> GetOptions(string name) =>
        _options.TryGetValue(name, out var values) ? values : [];

    public string? PositionalAt(int index) => index < _positional.Count ? _positional[index] : null;
}

/// <summary>Raised for command line misuse; mapped to exit code 1.</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}
