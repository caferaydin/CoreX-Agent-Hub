using System.Globalization;

namespace CoreX.Hub.Cli;

/// <summary>
/// Deterministic, script friendly output. Plain text, fixed column order, no colours,
/// no box drawing (architecture section 25: the CLI is a tool, not a dashboard).
/// </summary>
public static class Output
{
    public static void WriteTable(TextWriter output, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(headers);

        var materialized = rows?.ToList() ?? [];

        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in materialized)
        {
            for (var i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                widths[i] = Math.Max(widths[i], (row[i] ?? string.Empty).Length);
            }
        }

        output.WriteLine(FormatRow(headers.ToArray(), widths));

        foreach (var row in materialized)
        {
            output.WriteLine(FormatRow(row, widths));
        }

        if (materialized.Count == 0)
        {
            output.WriteLine("(no rows)");
        }
    }

    public static void WriteField(TextWriter output, string label, string? value)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label,-18}{value ?? string.Empty}"));
    }

    private static string FormatRow(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        var parts = new List<string>(cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i] ?? string.Empty;
            parts.Add(i == cells.Count - 1 ? cell : cell.PadRight(widths[i]));
        }

        return string.Join("  ", parts).TrimEnd();
    }
}

/// <summary>Stable process exit codes so the CLI can be scripted.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>Command line misuse.</summary>
    public const int Usage = 1;

    /// <summary>Domain or catalog operation refused the request.</summary>
    public const int OperationFailed = 2;

    /// <summary>The ready gate reported violations; the task stays in Draft.</summary>
    public const int ReadyGateNotSatisfied = 3;

    /// <summary>Unexpected failure.</summary>
    public const int Unexpected = 70;
}
