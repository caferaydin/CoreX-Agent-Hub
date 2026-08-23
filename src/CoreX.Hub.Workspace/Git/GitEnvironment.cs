namespace CoreX.Hub.Workspace.Git;

/// <summary>
/// Deterministic git runtime configuration for every managed invocation.
///
/// Two problems are solved here.
///
/// First, developer machine configuration must not leak into managed execution: no
/// developer aliases, hooks, credential helpers or global settings. The developer's own
/// global and system configuration files are never modified; they are simply not read.
///
/// Second, and this is the security critical one: git worktree add itself runs
/// post-checkout. A repository that configures its own core.hooksPath therefore gets to
/// execute code during the very first checkout, before any later hardening step could
/// possibly apply. Reproduced on git 2.54.0.windows.1.
///
/// PHASE-3 SECURITY CLARIFICATION: Hub-controlled hooks configuration must already be
/// effective during the FIRST checkout-capable git command.
///
/// The command scoped configuration environment (GIT_CONFIG_COUNT / GIT_CONFIG_KEY_n /
/// GIT_CONFIG_VALUE_n) has the same precedence as -c and is applied before the command
/// runs, which is exactly what is needed. It is used instead of "-c core.hooksPath"
/// because E.1 #8 forbids that command line construct as a bypass mechanism.
///
/// Hooks are not git's only execution surface. A repository can also name external
/// programs through core.fsmonitor, through a signing program, through diff helpers and
/// through clean/smudge filter drivers. The first three are neutralised here and by the
/// diff flags; filter drivers cannot be neutralised reliably, so a repository that
/// configures one is rejected by the safety preflight instead.
/// </summary>
public static class GitEnvironment
{
    /// <summary>Variables inherited from the parent so git.exe can run at all.</summary>
    public static IReadOnlyList<string> InheritedAllowlist { get; } =
        ["SystemRoot", "windir", "TEMP", "TMP", "PATH", "PATHEXT", "ProgramFiles",
         "ProgramFiles(x86)", "LOCALAPPDATA", "NUMBER_OF_PROCESSORS", "COMSPEC"];

    /// <summary>
    /// Deterministic machine identity for Hub created result commits, fixed by the
    /// architecture (ADIM 3: <c>user.name="CoreX Agent Hub"</c>,
    /// <c>user.email="agent@corex.local"</c>).
    ///
    /// This is not a contact address and must never be replaced by a person, an
    /// organisation mailbox, the current Windows user or the developer's global git
    /// identity: a result commit has to stay mechanically attributable to the Hub.
    /// </summary>
    public const string HubCommitterName = "CoreX Agent Hub";

    /// <inheritdoc cref="HubCommitterName" />
    public const string HubCommitterEmail = "agent@corex.local";

    /// <summary>
    /// Base configuration for every managed git command: no system config, an empty
    /// Hub owned global config, and no interactive prompting.
    /// </summary>
    public static Dictionary<string, string> Base(string emptyGlobalConfigPath) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = emptyGlobalConfigPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

    /// <summary>
    /// The configuration a managed AGENT child process must start from.
    ///
    /// Deliberately the same values as <see cref="Base" />, and deliberately expressed as a
    /// forward to it rather than as a second literal set: the git the Hub runs and the git
    /// an agent runs itself have to see one configuration, not two.
    ///
    /// The asymmetry this closes was measured, not imagined. Everything above was supplied
    /// per Hub command through the environment, so a child process that ran git on its own
    /// read the developer's system and global files instead: a credential helper the Hub
    /// had reset, a line ending policy the Hub did not apply, filter drivers the Hub never
    /// saw. The same worktree answered two different questions depending on who asked.
    ///
    /// It is not a boundary against a deliberate agent - configuration is caller
    /// overridable by anything holding a shell - and it is not claimed as one. It makes
    /// the child's starting configuration deterministic and equal to the Hub's.
    /// </summary>
    public static Dictionary<string, string> ForAgentChild(string emptyGlobalConfigPath) =>
        Base(emptyGlobalConfigPath);

    /// <summary>
    /// Base configuration plus command scoped overrides that must be effective before the
    /// command executes. Used for every checkout capable command.
    /// </summary>
    public static Dictionary<string, string> WithHardenedConfig(
        string emptyGlobalConfigPath,
        string hubHooksDirectory)
    {
        var environment = Base(emptyGlobalConfigPath);

        var overrides = new (string Key, string Value)[]
        {
            (GitConfigKeys.HooksPath, hubHooksDirectory),
            (GitConfigKeys.LongPaths, "true"),
            (GitConfigKeys.GcAuto, "0"),
            (GitConfigKeys.QuotePath, "false"),

            // A repository-local core.fsmonitor names an external helper that git
            // consults during status and index refresh. Managed commands never use one.
            (GitConfigKeys.FsMonitor, "false"),

            // Hub result commits never inherit repository or user signing policy, which
            // would otherwise invoke a configured signing program.
            (GitConfigKeys.CommitGpgSign, "false"),
            (GitConfigKeys.TagGpgSign, "false"),
        };

        environment["GIT_CONFIG_COUNT"] = overrides.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        for (var i = 0; i < overrides.Length; i++)
        {
            var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment["GIT_CONFIG_KEY_" + index] = overrides[i].Key;
            environment["GIT_CONFIG_VALUE_" + index] = overrides[i].Value;
        }

        return environment;
    }

    /// <summary>Fixed Hub identity for the result commit. No -c on the command line.</summary>
    public static Dictionary<string, string> WithHubIdentity(Dictionary<string, string> environment)
    {
        environment["GIT_AUTHOR_NAME"] = HubCommitterName;
        environment["GIT_AUTHOR_EMAIL"] = HubCommitterEmail;
        environment["GIT_COMMITTER_NAME"] = HubCommitterName;
        environment["GIT_COMMITTER_EMAIL"] = HubCommitterEmail;
        return environment;
    }
}

/// <summary>Configuration keys the Hub owns. Kept as data so no key is ever inlined.</summary>
public static class GitConfigKeys
{
    public const string HooksPath = "core.hooksPath";
    public const string LongPaths = "core.longpaths";
    public const string GcAuto = "gc.auto";
    public const string QuotePath = "core.quotePath";
    public const string FsMonitor = "core.fsmonitor";
    public const string CommitGpgSign = "commit.gpgSign";
    public const string TagGpgSign = "tag.gpgSign";
    public const string PushUrl = "remote.origin.pushurl";
    public const string CredentialHelper = "credential.helper";
    public const string WorktreeConfigExtension = "extensions.worktreeConfig";
    public const string CoreWorktree = "core.worktree";
    public const string CoreBare = "core.bare";

    /// <summary>Unroutable destination so an accidental publish cannot reach a remote.</summary>
    public const string DisabledPushUrl = "corex-hub://push-disabled";
}
