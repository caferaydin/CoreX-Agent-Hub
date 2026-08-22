using System.Globalization;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Appends human readable acceptance evidence (timings, pids, byte counts) to a file so
/// the phase report can quote measured values instead of describing them.
/// Test-only diagnostics; nothing in production depends on it.
/// </summary>
internal static class EvidenceLog
{
    private static readonly Lock Gate = new();

    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corex-phase2-evidence.txt");

    public static void Write(string line)
    {
        lock (Gate)
        {
            File.AppendAllText(
                Path,
                string.Create(CultureInfo.InvariantCulture, $"{line}{Environment.NewLine}"));
        }
    }
}
