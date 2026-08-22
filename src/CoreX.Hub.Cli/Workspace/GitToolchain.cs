namespace CoreX.Hub.Cli.Workspace;

/// <summary>
/// Resolves the real git.exe.
///
/// ProcessRunner deliberately does not search PATH, so that the executable it launches is
/// deterministic and independent of the child environment. Resolution therefore happens
/// here, in the composition layer, and the result is always a fully qualified real
/// executable - never git.cmd, never a shim, never a shell.
/// </summary>
public static class GitToolchain
{
    public const string OverrideEnvironmentVariable = "COREX_HUB_GIT_PATH";

    private const string GitExecutableName = "git.exe";

    /// <summary>Well known Windows install locations, probed in order.</summary>
    public static IReadOnlyList<string> WellKnownRelativePaths { get; } =
        [Path.Combine("Git", "cmd", GitExecutableName), Path.Combine("Git", "bin", GitExecutableName)];

    public static bool TryResolve(out string? gitExecutablePath, out string diagnostic)
    {
        var explicitPath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (IsUsable(explicitPath))
            {
                gitExecutablePath = explicitPath.Trim();
                diagnostic = $"{OverrideEnvironmentVariable}={gitExecutablePath}";
                return true;
            }

            gitExecutablePath = null;
            diagnostic = $"{OverrideEnvironmentVariable} is set to '{explicitPath}', which is not a usable git.exe.";
            return false;
        }

        foreach (var root in ProgramFileRoots())
        {
            foreach (var relative in WellKnownRelativePaths)
            {
                var candidate = Path.Combine(root, relative);
                if (IsUsable(candidate))
                {
                    gitExecutablePath = candidate;
                    diagnostic = "well-known install location";
                    return true;
                }
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim(), GitExecutableName);
            if (IsUsable(candidate))
            {
                gitExecutablePath = candidate;
                diagnostic = "resolved from PATH by the composition layer";
                return true;
            }
        }

        gitExecutablePath = null;
        diagnostic = $"git.exe was not found. Set {OverrideEnvironmentVariable} to its full path.";
        return false;
    }

    private static IEnumerable<string> ProgramFileRoots()
    {
        foreach (var name in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static bool IsUsable(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && string.Equals(Path.GetFileName(candidate.Trim()), GitExecutableName, StringComparison.OrdinalIgnoreCase)
        && File.Exists(candidate.Trim());
}
