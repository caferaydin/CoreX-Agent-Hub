using System.Globalization;
using CoreX.Hub.Composition.Isolation;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// Where a hard isolated workspace lives inside the environment, and what each part is called.
///
/// Every path is derived from a trusted Hub identity and from nothing a repository, a task or an
/// agent can influence. That is not tidiness: a path segment taken from an objective or a
/// repository display name is a path segment somebody else chooses, and the first thing they
/// would choose is a way out of the root it was supposed to be under.
///
/// The shape separates the three things that have different owners:
///
///   control/     Hub owned git configuration and the empty hooks directory
///   stage/       transfer artefacts, service only, deleted after use
///   repositories/&lt;id&gt;/git   the controlled bare repository, service only
///   worktrees/&lt;id&gt;          the disposable agent worktree, the only thing the agent sees
///
/// Nothing here touches the machine; it is arithmetic on strings, so a test can check the whole
/// layout without a distribution.
/// </summary>
public sealed record HardWorkspaceLayout
{
    private HardWorkspaceLayout(string executionRoot)
    {
        ExecutionRoot = executionRoot;
        ControlRoot = executionRoot + "/control";
        HooksDirectory = ControlRoot + "/hooks";
        EmptyGlobalConfigPath = ControlRoot + "/empty.gitconfig";
        StageRoot = executionRoot + "/stage";
        RepositoriesRoot = executionRoot + "/repositories";
        WorktreesRoot = executionRoot + "/worktrees";
    }

    public string ExecutionRoot { get; }

    /// <summary>Hub owned git control data. Never derived from repository content.</summary>
    public string ControlRoot { get; }

    /// <summary>
    /// An empty directory that is every managed command's <c>core.hooksPath</c>.
    ///
    /// Empty is the whole point. A repository that configures its own hooks path would
    /// otherwise get to run code during the first checkout, before any later hardening could
    /// apply - which is the failure the Windows workspace measured and closed, and this is the
    /// same closure on the other side of the boundary.
    /// </summary>
    public string HooksDirectory { get; }

    /// <summary>A file that exists and says nothing, standing in for a global git config.</summary>
    public string EmptyGlobalConfigPath { get; }

    public string StageRoot { get; }

    public string RepositoriesRoot { get; }

    public string WorktreesRoot { get; }

    public static HardWorkspaceLayout For(string executionRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionRoot);

        if (!LinuxPath.TryNormalize(executionRoot, out var segments, out var problem))
        {
            throw new ArgumentException(
                $"the execution root is not usable as a hard workspace root: {problem}", nameof(executionRoot));
        }

        return new HardWorkspaceLayout(LinuxPath.Join(segments));
    }

    /// <summary>The controlled bare repository for one catalogue repository.</summary>
    public string BareRepository(Guid repositoryId) =>
        RepositoriesRoot + "/" + Identity(repositoryId) + "/git";

    /// <summary>
    /// The disposable worktree for one attempt.
    ///
    /// Named from the execution's own identity rather than from the task key, so two attempts,
    /// two tasks or two organizations can never derive the same directory - and so no display
    /// name reaches the filesystem.
    /// </summary>
    public string Worktree(Guid executionIdentity) => WorktreesRoot + "/" + Identity(executionIdentity);

    /// <summary>The transfer artefact for one preparation. Unique, and deleted afterwards.</summary>
    public string TransferArtefact(Guid transferIdentity) =>
        StageRoot + "/" + Identity(transferIdentity) + ".bundle";

    /// <summary>
    /// A Hub id rendered as a path segment.
    ///
    /// Thirty-two hexadecimal characters: inside every filesystem's alphabet by construction, so
    /// there is nothing to escape and therefore no escaping to get wrong.
    /// </summary>
    public static string Identity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "a hard workspace path needs a trusted identity; an empty one would name every workspace "
                + "the same directory.",
                nameof(id));
        }

        return id.ToString("N", CultureInfo.InvariantCulture);
    }
}
