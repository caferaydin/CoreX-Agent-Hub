namespace CoreX.Hub.Agents.ClaudeCode;

/// <summary>
/// What this runtime needs to know about the local installation, and nothing else.
///
/// Deliberately absent: any credential, token, key, endpoint, base URL, account or
/// billing concept. Authentication is out of band and stays there - the tool already
/// holds a signed in workstation session under the user profile, and the Hub neither
/// reads it, copies it, stores it nor configures an alternative to it (ADR-0009).
///
/// Also deliberately absent: the tool list, the permission mode, the model and the
/// effort. Two of those are frozen by ADR-0020 and may not become configuration; the
/// other two are catalog facts that arrive per run on the request, so a profile change is
/// the only way to change them.
/// </summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>
    /// Absolute path to the installed executable.
    ///
    /// Never a name to be resolved on PATH: the child environment is deny by default and
    /// the process runner performs no lookup, so what runs has to be decided here, where
    /// an operator can read it, rather than by whatever happens to be earlier on a path.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// An existing directory inside the runner's trust root that the read only health
    /// probes run in.
    ///
    /// Health asks the local installation about itself, which still needs somewhere to
    /// stand. It is never a workspace and nothing is written to it.
    /// </summary>
    public required string HealthProbeDirectory { get; init; }
}
