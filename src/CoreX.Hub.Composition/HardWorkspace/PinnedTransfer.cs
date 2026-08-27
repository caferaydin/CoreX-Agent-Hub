using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Workspace.Abstractions;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>What a transfer produced, or why there is nothing to transfer.</summary>
/// <param name="Encoded">The artefact, base64 encoded, ready for the text channel.</param>
internal sealed record PinnedTransferResult(bool Succeeded, string Encoded, string Detail)
{
    public static PinnedTransferResult Refused(string detail) => new(false, string.Empty, detail);
}

/// <summary>
/// Moves one pinned commit out of the Windows repository and nothing else.
///
/// The agent never sees the source repository, and this is why: what leaves the host is a git
/// bundle - committed objects reachable from one ref, and nothing more. A bundle cannot contain
/// a working tree, so the developer's uncommitted edits, untracked files and ignored files are
/// not omitted by policy here, they are simply not the kind of thing the format carries. That is
/// a stronger guarantee than a copy that tries to exclude them.
///
/// The pin is verified rather than assumed. A bundle has to name a ref - git refuses to build
/// one from a bare object id - so the branch the preflight resolved is bundled and the artefact
/// is then asked what it actually contains. If the branch moved between the preflight and here,
/// the answer differs from the commit that was pinned and the transfer is refused rather than
/// quietly carrying a different one.
///
/// The artefact itself lives in the Hub's own data directory, under a unique name, and is
/// deleted whether the transfer succeeded or failed. It is never placed inside a worktree, and
/// its path is never given to the environment: the bytes go through the control channel.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PinnedTransfer
{
    private readonly IGitCommandExecutor _git;
    private readonly string _stagingRoot;
    private readonly string _emptyGlobalConfigPath;
    private readonly string _hooksDirectory;

    public PinnedTransfer(
        IGitCommandExecutor git,
        string stagingRoot,
        string emptyGlobalConfigPath,
        string hooksDirectory)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _stagingRoot = stagingRoot;
        _emptyGlobalConfigPath = emptyGlobalConfigPath;
        _hooksDirectory = hooksDirectory;
    }

    /// <summary>
    /// Builds the artefact for one pinned commit.
    ///
    /// <paramref name="ref"/> is the branch the preflight resolved, and
    /// <paramref name="pinnedCommitSha"/> is what it resolved to. Both are needed: the first is
    /// what git can bundle, the second is what the result is checked against.
    /// </summary>
    public async Task<PinnedTransferResult> CreateAsync(
        string repositoryPath,
        string reference,
        string pinnedCommitSha,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_stagingRoot);

        var artefact = Path.Combine(
            _stagingRoot,
            "pinned-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture) + ".bundle");

        try
        {
            var created = await RunAsync(
                repositoryPath, ["bundle", "create", artefact, reference], ct);

            if (!created.Succeeded)
            {
                return PinnedTransferResult.Refused(
                    $"the pinned commit could not be packaged for transfer ({created.Describe()})");
            }

            var heads = await RunAsync(repositoryPath, ["bundle", "list-heads", artefact], ct);

            if (!heads.Succeeded)
            {
                return PinnedTransferResult.Refused(
                    $"the transfer artefact could not be read back ({heads.Describe()})");
            }

            if (!Contains(heads.Stdout, pinnedCommitSha))
            {
                // The branch moved between the preflight and now. The commit that was written
                // down as this execution's provenance is the authority, so an artefact carrying
                // a different one is refused rather than transferred.
                return PinnedTransferResult.Refused(
                    $"the transfer artefact does not carry the pinned commit '{pinnedCommitSha}'; "
                    + "the source branch moved after it was resolved.");
            }

            return new PinnedTransferResult(
                true,
                Convert.ToBase64String(await File.ReadAllBytesAsync(artefact, ct)),
                $"transferred the pinned commit from '{reference}'");
        }
        finally
        {
            Delete(artefact);
        }
    }

    /// <summary>Whether any ref in the artefact resolves to exactly the pinned commit.</summary>
    private static bool Contains(string listHeads, string pinnedCommitSha) =>
        listHeads
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.StartsWith(pinnedCommitSha, StringComparison.OrdinalIgnoreCase));

    private Task<GitCommandResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken ct) =>
        _git.ExecuteAsync(
            new GitCommand
            {
                WorkingDirectory = repositoryPath,
                Arguments = arguments,

                // The same hardened configuration every other managed git command gets. Building
                // a bundle reads the object database rather than the working tree, but it is
                // still git running against a repository whose configuration the Hub does not
                // trust, and there is no reason for this one command to be the exception.
                Environment = GitEnvironment.WithHardenedConfig(_emptyGlobalConfigPath, _hooksDirectory),
                Timeout = TimeSpan.FromMinutes(10),
            },
            ct);

    private static void Delete(string artefact)
    {
        try
        {
            if (File.Exists(artefact))
            {
                File.Delete(artefact);
            }
        }
        catch (IOException)
        {
            // A locked artefact is a hygiene problem, not a security one: it contains committed
            // objects the Hub already had, and it is inside the Hub's own data directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
