namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// The runtime half of architecture invariant E.1 #3 (ADR-0006): no shell, ever.
///
/// The source guard proves production code contains no shell launch construct. That is
/// necessary but not sufficient: ProcessRunner takes an executable and an argument vector
/// from its caller, so a caller could still hand it "C:/Windows/System32/cmd.exe" with
/// "/c ..." and obtain a shell through a perfectly clean source tree. This classifier
/// closes that gap at the request boundary, before any process is created.
///
/// It is deliberately a short, explicit, hard coded classification - not an allowlist, not
/// a policy DSL, not a plugin point, and not another process launch abstraction. Adding a
/// name means adding a line here.
///
/// This file is the single place allowed to name a shell, which is why the source scanner
/// exempts it and separately asserts the exemption is not vacuous.
/// </summary>
public static class ShellExecutablePolicy
{
    /// <summary>
    /// Direct shell interpreters, matched on the file name without its extension so that
    /// both "cmd" and "cmd.exe" are covered. Windows executable names are compared case
    /// insensitively, so "CMD.EXE" is the same thing.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenExecutableBaseNames { get; } =
        ["cmd", "powershell", "pwsh", "bash", "sh"];

    /// <summary>
    /// Script and shim inputs that cannot be launched without an interpreter. Phase 2
    /// deliberately does not resolve npm.cmd style shims; a future real consumer will map
    /// them to a real executable outside this shell path.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenExtensions { get; } =
        [".cmd", ".bat", ".ps1", ".psm1", ".sh", ".vbs", ".js"];

    /// <summary>
    /// Classifies an executable request. Pure string classification: it never touches the
    /// filesystem, so an uninstalled interpreter is still classified correctly.
    /// </summary>
    public static bool IsShellOrScript(string? candidate, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var path = candidate.Trim();
        var extension = Path.GetExtension(path);

        if (ForbiddenExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            reason =
                $"'{path}' is a script or shim ('{extension}') that cannot be launched without a shell. "
                + "Resolve it to a real executable instead (ADR-0006, E.1 #3).";
            return true;
        }

        var baseName = Path.GetFileNameWithoutExtension(path);

        if (ForbiddenExecutableBaseNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            reason =
                $"'{path}' is a direct shell interpreter ('{baseName}'). "
                + "ProcessRunner executes an executable plus an argument vector and never a shell (ADR-0006, E.1 #3).";
            return true;
        }

        return false;
    }
}
