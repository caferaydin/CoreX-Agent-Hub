using System.Globalization;
using CoreX.Hub.Workspace.Git;

namespace CoreX.Hub.Composition.HardWorkspace;

/// <summary>
/// Every command the hard workspace may run inside the environment, and no others.
///
/// A closed surface, deliberately. The alternative - a general "run this Linux command" the
/// application or a provider adapter could reach - would make every other guarantee in this
/// slice advisory: the ownership model, the hardened git configuration and the absence of a
/// shell all rest on nobody being able to ask for something else.
///
/// Two things are shared with the Windows workspace rather than restated here, because they are
/// policy and not platform: the git configuration keys the Hub owns
/// (<see cref="GitConfigKeys"/>) and the Hub's commit identity
/// (<see cref="GitEnvironment.HubCommitterName"/>). A second literal copy of either would be a
/// second policy, and the day they disagreed would be the day one of them was wrong.
///
/// The git environment reaches the guest through <c>/usr/bin/env</c> rather than through the
/// host's environment. That is not a workaround: WSL forwards only what <c>WSLENV</c> names, so
/// a host variable is simply not there on the other side - and <c>env</c> states the whole
/// environment as argv, which is both explicit and unaffected by whatever the host happens to
/// have set.
/// </summary>
public static class HardGitCommands
{
    public const string Git = "/usr/bin/git";
    public const string Env = "/usr/bin/env";
    public const string OpenSsl = "/usr/bin/openssl";
    public const string Mkdir = "/usr/bin/mkdir";
    public const string Chown = "/usr/bin/chown";
    public const string Chmod = "/usr/bin/chmod";
    public const string Rm = "/usr/bin/rm";
    public const string Stat = "/usr/bin/stat";
    public const string Touch = "/usr/bin/touch";
    public const string Realpath = "/usr/bin/realpath";

    /// <summary>
    /// Turns off external diff and textconv on every evidence command.
    ///
    /// Not optional, and the reason is the same one the Windows workspace records: a repository
    /// that configures <c>diff.external</c> or a textconv driver would otherwise run a program of
    /// its own choosing while the Hub is collecting evidence about it.
    /// </summary>
    public const string NoExternalDiff = "--no-ext-diff";

    public const string NoTextConv = "--no-textconv";

    /// <summary>
    /// The Hub's git configuration, expressed as an <c>env</c> prefix.
    ///
    /// <c>-i</c> starts from nothing, so the guest's own login environment cannot contribute a
    /// credential helper, an alias or a proxy. What is put back is the minimum git needs to run
    /// plus the Hub's policy, and the policy half is the same key set the Windows side applies -
    /// hooks path, no auto gc, no fsmonitor, no signing.
    ///
    /// The configuration travels as <c>GIT_CONFIG_COUNT</c> / <c>KEY</c> / <c>VALUE</c> rather
    /// than as <c>-c</c> arguments. Same precedence, and it is effective before the command runs,
    /// which is what makes it apply to the very first checkout-capable command instead of only
    /// afterwards.
    /// </summary>
    public static IReadOnlyList<string> Hardened(HardWorkspaceLayout layout, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(layout);

        (string Key, string Value)[] overrides =
        [
            (GitConfigKeys.HooksPath, layout.HooksDirectory),
            (GitConfigKeys.GcAuto, "0"),
            (GitConfigKeys.QuotePath, "false"),
            (GitConfigKeys.FsMonitor, "false"),
            (GitConfigKeys.CommitGpgSign, "false"),
            (GitConfigKeys.TagGpgSign, "false"),
        ];

        var command = new List<string>
        {
            Env,
            "-i",

            // git needs to find its own helpers and a place it may call home; both are Hub
            // owned. HOME points at the control directory so that a stray global configuration
            // in a real account's home can never be read.
            "PATH=/usr/bin:/bin",
            "HOME=" + layout.ControlRoot,
            "GIT_CONFIG_NOSYSTEM=1",
            "GIT_CONFIG_GLOBAL=" + layout.EmptyGlobalConfigPath,
            "GIT_TERMINAL_PROMPT=0",
            "GIT_OPTIONAL_LOCKS=0",
            "GIT_CONFIG_COUNT=" + overrides.Length.ToString(CultureInfo.InvariantCulture),
        };

        for (var i = 0; i < overrides.Length; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            command.Add("GIT_CONFIG_KEY_" + index + "=" + overrides[i].Key);
            command.Add("GIT_CONFIG_VALUE_" + index + "=" + overrides[i].Value);
        }

        command.Add(Git);
        command.AddRange(arguments);

        return command;
    }

    /// <summary>The same environment, plus the Hub's fixed commit identity.</summary>
    public static IReadOnlyList<string> HardenedWithHubIdentity(
        HardWorkspaceLayout layout,
        params string[] arguments)
    {
        var command = Hardened(layout).ToList();
        var gitIndex = command.IndexOf(Git);

        // Inserted before the executable so they are environment assignments rather than
        // arguments, and never as -c on the command line, which E.1 #8 forbids as a bypass.
        command.InsertRange(gitIndex, [
            "GIT_AUTHOR_NAME=" + GitEnvironment.HubCommitterName,
            "GIT_AUTHOR_EMAIL=" + GitEnvironment.HubCommitterEmail,
            "GIT_COMMITTER_NAME=" + GitEnvironment.HubCommitterName,
            "GIT_COMMITTER_EMAIL=" + GitEnvironment.HubCommitterEmail,
        ]);

        command.AddRange(arguments);
        return command;
    }

    /// <summary>Decodes the transferred artefact into the staging directory.</summary>
    public static IReadOnlyList<string> DecodeTransfer(string destination) =>
        [OpenSsl, "base64", "-d", "-A", "-out", destination];

    /// <summary>
    /// Confirms what the transferred artefact actually contains.
    ///
    /// Asked on both sides of the boundary. The host verifies the artefact it made carries the
    /// pinned commit; the environment verifies the artefact it received carries the same one. A
    /// single check would leave the transfer itself unexamined.
    /// </summary>
    public static IReadOnlyList<string> ListTransferHeads(string artefact) =>
        [Git, "bundle", "list-heads", artefact];

    /// <summary>Resolves a path physically, so a symlink cannot pretend to be inside a root.</summary>
    public static IReadOnlyList<string> Resolve(string path) => [Realpath, "-m", path];
}
