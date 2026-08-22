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
}
