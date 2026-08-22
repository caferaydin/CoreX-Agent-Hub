using System.Text;

namespace CoreX.Hub.Workspace.Git;

/// <summary>
/// Materialises the Hub owned hooks directory.
///
/// It contains exactly one hook: a pre-push blocker that always rejects. There is
/// deliberately NO pre-commit hook, because the Hub has to be able to create its own
/// result commit without ever bypassing its own protection (ADR-0005, E.1 #8).
///
/// Pointing core.hooksPath at this directory has a second effect that matters more than
/// the blocker itself: hooks committed into the repository become unreachable.
/// </summary>
public static class HubHooks
{
    public const string HooksDirectoryName = "_hub-hooks";
    public const string GlobalConfigFileName = "hub-empty.gitconfig";
    public const string PrePushHookName = "pre-push";

    /// <summary>
    /// Written with LF endings and no BOM: git for windows runs hooks through its own
    /// bundled sh, and a CRLF shebang is not a usable interpreter line.
    /// </summary>
    private static readonly string PrePushHookContent = string.Join(
        '\n',
        [
            "#!/bin/sh",
            "# CoreX Agent Hub: publishing is never performed by the Hub and never by an agent.",
            "echo \"corex-hub: push is disabled for managed worktrees\" 1>&2",
            "exit 1",
            string.Empty,
        ]);

    /// <summary>Creates or refreshes the hooks directory. Idempotent.</summary>
    public static HubHooksLayout Materialise(string hubControlRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubControlRoot);

        var hooksDirectory = Path.Combine(hubControlRoot, HooksDirectoryName);
        Directory.CreateDirectory(hooksDirectory);

        var prePush = Path.Combine(hooksDirectory, PrePushHookName);
        WriteIfDifferent(prePush, PrePushHookContent);

        var globalConfig = Path.Combine(hubControlRoot, GlobalConfigFileName);
        if (!File.Exists(globalConfig))
        {
            File.WriteAllText(globalConfig, string.Empty, new UTF8Encoding(false));
        }

        return new HubHooksLayout(hooksDirectory, prePush, globalConfig);
    }

    private static void WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}

/// <summary>Where the Hub owned git control files live for a workspace root.</summary>
public sealed record HubHooksLayout(string HooksDirectory, string PrePushHookPath, string EmptyGlobalConfigPath);
