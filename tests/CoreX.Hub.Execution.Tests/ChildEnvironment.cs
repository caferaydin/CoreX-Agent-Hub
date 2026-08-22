using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Runs the fixture in print-env mode and returns what the child process actually saw.
/// Reading the real child environment is the point: asserting on an internal dictionary
/// would not prove isolation.
/// </summary>
internal static class ChildEnvironment
{
    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        TempWorkspace workspace,
        IReadOnlyList<string> allowlist,
        IReadOnlyDictionary<string, string>? additional = null)
    {
        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "print-env") with
        {
            EnvironmentAllowlist = allowlist,
            AdditionalEnvironment = additional
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(ProcessTerminationReason.Exited, result.TerminationReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ENV-END", result.NormalizedStdout, StringComparison.Ordinal);

        var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in result.NormalizedStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                observed[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }

        return observed;
    }
}
