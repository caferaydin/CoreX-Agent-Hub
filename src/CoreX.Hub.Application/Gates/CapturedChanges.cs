namespace CoreX.Hub.Application.Gates;

/// <summary>One added line from the diff, with enough context to report a hit.</summary>
public sealed record AddedLine(string RelativePath, int LineNumber, string Text);

/// <summary>
/// The machine facts the gates evaluate, captured before any of them runs.
///
/// Deliberately bounded. There is no task aggregate, no execution aggregate, no runtime
/// and no provider information here: the gates judge what git observed, not who produced
/// it, so a local harness and a hosted assistant are evaluated identically.
///
/// Everything in here comes from the Phase 3 workspace capture. Nothing in this module
/// runs git, touches the filesystem or writes evidence to obtain it.
/// </summary>
public sealed record CapturedChanges
{
    /// <summary>The commit the workspace was pinned to.</summary>
    public required string BaseCommitSha { get; init; }

    /// <summary>Where HEAD actually is now. This is the authority for K1.</summary>
    public required string ObservedHeadSha { get; init; }

    /// <summary>Commits observed between the base and HEAD, as captured. Never invented.</summary>
    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    /// <summary>
    /// Repository relative paths that changed, in the machine normalised form the
    /// workspace produced. Modified, added, deleted and both sides of a rename.
    /// </summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    /// <summary>
    /// Added diff lines only. Deleted lines and untouched history are out of scope for
    /// K3 by design: the gate asks what this execution is about to introduce.
    /// </summary>
    public IReadOnlyList<AddedLine> AddedLines { get; init; } = [];
}
