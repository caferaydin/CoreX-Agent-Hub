namespace CoreX.Hub.Agents.Codex;

/// <summary>
/// What installing this runtime on one machine actually needs.
///
/// Two facts and nothing else. There is no credential, no token, no endpoint and no account
/// here: authentication is the workstation's own Codex session, out of band, and this
/// adapter neither reads it nor replaces it.
///
/// The version is configuration rather than a constant because it is a deployment fact -
/// which build is installed at that path - and the adapter refuses to run against anything
/// else. It is not a range and not a floor.
/// </summary>
public sealed record CodexOptions
{
    /// <summary>
    /// Absolute path to the pinned executable.
    ///
    /// Never a bare name and never resolved on PATH. On a developer machine PATH commonly
    /// holds a newer Codex, and that newer Codex is not an accepted reviewer runtime - so
    /// picking whichever one happened to be first would silently run the review on a build
    /// that was measured and rejected.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// The exact version this adapter was measured against and will run.
    ///
    /// Exact, not a minimum. Codex changed the behaviour this reviewer depends on between
    /// releases in both directions, so "at least" would be a guess about builds nobody has
    /// tried here.
    /// </summary>
    public required string AcceptedVersion { get; init; }

    /// <summary>A directory the health probe may run in. Never inside a reviewed tree.</summary>
    public required string HealthProbeDirectory { get; init; }
}
