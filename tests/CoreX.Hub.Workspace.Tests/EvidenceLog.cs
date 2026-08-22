using System.Globalization;

namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Appends measured acceptance evidence so the phase report can quote real values.
/// Test-only diagnostics.
/// </summary>
internal static class EvidenceLog
{
    private static readonly Lock Gate = new();

    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corex-phase3-evidence.txt");

    public static void Write(string line)
    {
        lock (Gate)
        {
            File.AppendAllText(Path, string.Create(CultureInfo.InvariantCulture, $"{line}{Environment.NewLine}"));
        }
    }
}
