using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Infrastructure.Evidence;
using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Cli.Verification;

/// <summary>
/// Where the Hub owned evidence and cache directories live, and how the configured
/// executable allowlist becomes the type the verification module consumes.
///
/// Both roots are under the data directory, never inside a source repository, an agent
/// worktree or a context repository: an agent must not be able to modify its own evidence
/// through ordinary worktree operations (Phase 4 brief section 26).
/// </summary>
public static class VerificationComposition
{
    public static string ResolveEvidenceRoot(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), "evidence");

    public static EvidenceBlobStore CreateEvidenceStore(string dataDirectory) =>
        new(ResolveEvidenceRoot(dataDirectory));

    public static FileVerificationBaselineCache CreateBaselineCache(string dataDirectory) =>
        new(dataDirectory);

    /// <summary>
    /// Turns the configured logical name to absolute path map into the fail closed
    /// allowlist. Configuration problems are surfaced, never silently dropped.
    /// </summary>
    public static ExecutableAllowlist CreateAllowlist(
        HubConfig config,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ExecutableAllowlist.Create(config.Verification.Executables, out problems);
    }
}
