using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Composition.Agents;

/// <summary>
/// The one place a runtime key becomes a runtime.
///
/// This is the composition seam the architecture depends on, and naming adapters is
/// exactly its job (ADR-0008). What it deliberately does not do is choose between them: it
/// registers what configuration declared, under the keys configuration gave, and hands
/// back a dictionary. There is no branch on a provider, no preference order, no discovery
/// and no fallback - a profile that names a key nobody declared gets an honest refusal
/// rather than whatever else happened to be registered.
///
/// It lives here rather than in the orchestration wiring so that orchestration source
/// stays provider blind in the literal sense: nothing under Orchestration mentions an
/// adapter at all.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentRuntimeRegistry
{
    /// <summary>
    /// Builds the registry from configuration, or explains what is wrong with it.
    ///
    /// Every runtime family gets its own section and its own construction, because what a
    /// family needs to be installable differs: a deterministic harness needs a fixture and a
    /// scenario, an installed tool needs the absolute path of the tool. Flattening them into
    /// one generic runtime list would mean inventing settings that mean nothing to half of
    /// them, and would put a provider switch into a configuration file.
    /// </summary>
    public static IReadOnlyDictionary<string, IAgentRuntime> Build(
        HubConfig config,
        string workspaceRoot,
        string gitConfigGlobalPath)
    {
        ArgumentNullException.ThrowIfNull(config);

        var runtimes = new Dictionary<string, IAgentRuntime>(StringComparer.OrdinalIgnoreCase);

        // A fresh adapter per runtime: each one owns a file system guard and a process
        // runner, and sharing one across runtimes would make the trust root of the first the
        // trust root of them all.
        IAgentProcessRunner Processes() => new ProcessAgentRunner(workspaceRoot, gitConfigGlobalPath);

        AddScripted(runtimes, config, Processes);
        ClaudeCodeRuntimeFactory.AddDeclared(runtimes, config, workspaceRoot, Processes);
        CodexRuntimeFactory.AddDeclared(runtimes, config, workspaceRoot, Processes);

        return runtimes;
    }

    /// <summary>
    /// Confirms the profile about to be used can actually be run here.
    ///
    /// The gateway would refuse an unknown key anyway, but by then a worktree exists and a
    /// context pack has been built. A configuration problem is not worth an execution row,
    /// so it is found before one is written.
    /// </summary>
    public static void EnsureResolvable(IReadOnlyDictionary<string, IAgentRuntime> runtimes, AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(profile);

        if (runtimes.ContainsKey(profile.RuntimeKey))
        {
            return;
        }

        var known = runtimes.Count == 0 ? "none are declared" : string.Join(", ", runtimes.Keys.Order());

        throw new HubConfigurationException(
            $"agent profile '{profile.Name}' names the runtime key '{profile.RuntimeKey}', which this "
            + $"installation does not declare (declared: {known}).");
    }

    /// <summary>
    /// Every scripted runtime key whose fixture lives inside an isolated environment.
    ///
    /// Asked by composition so that a runtime and an organization cannot be paired the wrong way
    /// round. The two mistakes are symmetric and both fail late and confusingly without this: a hard
    /// organization naming a host runtime would hand a Windows path to a Linux workload, and a
    /// logical organization naming an isolated one would try to launch a path this machine has no
    /// file at.
    /// </summary>
    public static IReadOnlySet<string> IsolatedRuntimeKeys(HubConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Agents.ScriptedRuntimes
            .Where(r => IsIsolated(r.FixtureLocation))
            .Select(r => r.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIsolated(string? location) =>
        string.Equals((location ?? string.Empty).Trim(), "Isolated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The fixture path, checked against the filesystem the declaration says it is on.
    ///
    /// A host fixture is confirmed to exist, which is what stops an execution row being written for
    /// a runtime that cannot start. An isolated fixture is checked for shape only - it is a path on
    /// another machine, and the machine that has it answers for it when the workload is launched
    /// there. Neither is normalised into the other: running the host's path resolution over a Linux
    /// path is what produced <c>D:\opt\...</c> out of <c>/opt/...</c>.
    /// </summary>
    private static string ResolveFixturePath(ScriptedRuntimeConfig declared)
    {
        var path = (declared.FixturePath ?? string.Empty).Trim();
        var location = (declared.FixtureLocation ?? string.Empty).Trim();

        if (location.Length > 0
            && !IsIsolated(location)
            && !string.Equals(location, "Host", StringComparison.OrdinalIgnoreCase))
        {
            throw new HubConfigurationException(
                $"scripted runtime '{declared.Key}' declares fixtureLocation '{declared.FixtureLocation}', "
                + "which is not one of: Host, Isolated.");
        }

        if (!IsIsolated(location))
        {
            var full = Path.GetFullPath(path);

            if (!File.Exists(full))
            {
                throw new HubConfigurationException(
                    $"scripted runtime '{declared.Key}' names a fixture executable that does not exist: {full}");
            }

            return full;
        }

        if (!path.StartsWith('/')
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)
            || path.Any(char.IsWhiteSpace)
            || path.Any(char.IsControl))
        {
            throw new HubConfigurationException(
                $"scripted runtime '{declared.Key}' is declared Isolated, so its fixture path must be one "
                + $"absolute path inside the environment carrying no arguments; '{declared.FixturePath}' is not.");
        }

        return path;
    }

    private static void AddScripted(
        IDictionary<string, IAgentRuntime> runtimes,
        HubConfig config,
        Func<IAgentProcessRunner> processes)
    {
        foreach (var declared in config.Agents.ScriptedRuntimes)
        {
            if (string.IsNullOrWhiteSpace(declared.Key))
            {
                throw new HubConfigurationException("a scripted runtime was declared without a key.");
            }

            if (runtimes.ContainsKey(declared.Key))
            {
                throw new HubConfigurationException(
                    $"more than one runtime is declared under the key '{declared.Key}'.");
            }

            // Written the way the fixture's own verbs are written - apply-patch, out-of-scope-write -
            // because that is what an operator reading either file would expect to match.
            var declaredMode = (declared.Mode ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);

            if (!Enum.TryParse<ScriptedAgentMode>(declaredMode, ignoreCase: true, out var mode))
            {
                throw new HubConfigurationException(
                    $"scripted runtime '{declared.Key}' declares mode '{declared.Mode}', which is not one of: "
                    + string.Join(", ", Enum.GetNames<ScriptedAgentMode>()));
            }

            var fixturePath = ResolveFixturePath(declared);

            runtimes[declared.Key] = new ScriptedAgentRuntime(
                new ScriptedAgentOptions { FixtureExecutablePath = fixturePath, Mode = mode },
                processes());
        }
    }
}
