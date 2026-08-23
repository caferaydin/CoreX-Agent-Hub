namespace CoreX.Hub.Workspace;

/// <summary>Tuning for the workspace manager. Small and explicit; not a policy engine.</summary>
public sealed record WorkspaceOptions
{
    /// <summary>
    /// Where the Hub owned git control files live: the hooks directory and the empty
    /// global config. Must be outside every managed repository.
    /// </summary>
    public required string HubControlRoot { get; init; }

    public TimeSpan GitTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Bounded retries before a stuck worktree is reported as orphaned.</summary>
    public int WorktreeRemovalAttempts { get; init; } = 3;

    public TimeSpan RemovalRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Files larger than this are inventoried by size only, never hashed.</summary>
    public long MaxHashedFileBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// How deep the working tree attribute scan descends. A bound, not a tuning knob: a
    /// directory cycle built out of junctions has to end somewhere, and a tree deeper than
    /// this is refused rather than partly scanned.
    /// </summary>
    public int AttributeScanMaxDepth { get; init; } = 64;

    /// <summary>
    /// How many working tree attribute files the scan will look at before it refuses.
    /// Exceeding it is a rejection, never a truncation: a scan that stopped early and
    /// reported nothing would be indistinguishable from a clean tree.
    /// </summary>
    public int MaxAttributeFilesScanned { get; init; } = 2_000;

    /// <summary>
    /// An attribute file larger than this is refused unread. Nothing legitimate needs it,
    /// and reading an arbitrary sized file to decide whether it is safe is not a bargain
    /// worth making.
    /// </summary>
    public long MaxAttributeFileBytes { get; init; } = 1024 * 1024;
}
