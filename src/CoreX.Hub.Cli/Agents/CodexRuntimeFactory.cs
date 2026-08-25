using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;

namespace CoreX.Hub.Cli.Agents;

/// <summary>
/// Builds the pinned Codex runtimes this machine declares.
///
/// The same composition-layer job the other factory does, with one extra refusal: a Codex
/// runtime must state the exact build it accepts. That is not ceremony. This provider changed
/// the behaviour a review depends on between releases, so "some Codex is installed" is not a
/// usable fact - and the build a developer machine has on PATH is usually a different one.
///
/// Every failure here happens before an execution row exists. A configured runtime whose
/// executable is missing is a one line fix for an operator, and finding it at composition is
/// far better than finding it inside a run that has already built a worktree.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CodexRuntimeFactory
{
    public static void AddDeclared(
        IDictionary<string, IAgentRuntime> runtimes,
        HubConfig config,
        string healthProbeDirectory,
        Func<IAgentProcessRunner> processes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(processes);

        foreach (var declared in config.Agents.CodexRuntimes)
        {
            if (string.IsNullOrWhiteSpace(declared.Key))
            {
                throw new HubConfigurationException("a codex runtime was declared without a key.");
            }

            if (runtimes.ContainsKey(declared.Key))
            {
                throw new HubConfigurationException(
                    $"more than one runtime is declared under the key '{declared.Key}'.");
            }

            if (string.IsNullOrWhiteSpace(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"codex runtime '{declared.Key}' declares no executable path. It is stated absolutely and "
                    + "never discovered on PATH, so there is nothing to fall back to.");
            }

            if (!Path.IsPathFullyQualified(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"codex runtime '{declared.Key}' names '{declared.ExecutablePath}', which is not an "
                    + "absolute path.");
            }

            if (!File.Exists(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"codex runtime '{declared.Key}' names an executable that does not exist: "
                    + declared.ExecutablePath);
            }

            if (string.IsNullOrWhiteSpace(declared.AcceptedVersion))
            {
                throw new HubConfigurationException(
                    $"codex runtime '{declared.Key}' states no accepted version. This provider is pinned to an "
                    + "exact build, and a runtime that would run whatever it found is not one.");
            }

            runtimes[declared.Key] = new CodexRuntime(
                new CodexOptions
                {
                    ExecutablePath = declared.ExecutablePath,
                    AcceptedVersion = declared.AcceptedVersion.Trim(),
                    HealthProbeDirectory = healthProbeDirectory,
                },
                processes());
        }
    }
}
