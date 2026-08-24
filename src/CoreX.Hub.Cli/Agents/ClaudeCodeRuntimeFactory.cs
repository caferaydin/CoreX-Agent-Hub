using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.ClaudeCode;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;

namespace CoreX.Hub.Cli.Agents;

/// <summary>
/// Builds the installed real runtimes this machine declares.
///
/// Composition layer work: it reads configuration, checks that what it names actually
/// exists, and hands back a runtime behind the shared interface. Nothing above it learns
/// which runtime it got, and nothing below it learns that configuration exists.
///
/// Every failure here is a refusal before anything runs. A configured runtime whose
/// executable is missing is a problem an operator can fix in one line, and saying so at
/// composition time is far better than discovering it inside an execution that has already
/// built a worktree.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClaudeCodeRuntimeFactory
{
    /// <summary>
    /// Adds every declared runtime to <paramref name="runtimes"/>.
    ///
    /// <paramref name="healthProbeDirectory"/> is an existing directory inside the runner's
    /// trust root; the read only health probes need somewhere to stand and never write
    /// there.
    /// </summary>
    public static void AddDeclared(
        IDictionary<string, IAgentRuntime> runtimes,
        HubConfig config,
        string healthProbeDirectory,
        Func<IAgentProcessRunner> processes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(processes);

        foreach (var declared in config.Agents.ClaudeCodeRuntimes)
        {
            if (string.IsNullOrWhiteSpace(declared.Key))
            {
                throw new HubConfigurationException("a runtime was declared without a key.");
            }

            if (runtimes.ContainsKey(declared.Key))
            {
                throw new HubConfigurationException(
                    $"more than one runtime is declared under the key '{declared.Key}'.");
            }

            if (string.IsNullOrWhiteSpace(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"runtime '{declared.Key}' declares no executable path. It is stated absolutely and never "
                    + "discovered on PATH, so there is nothing to fall back to.");
            }

            if (!Path.IsPathFullyQualified(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"runtime '{declared.Key}' names '{declared.ExecutablePath}', which is not an absolute path.");
            }

            if (!File.Exists(declared.ExecutablePath))
            {
                throw new HubConfigurationException(
                    $"runtime '{declared.Key}' names an executable that does not exist: {declared.ExecutablePath}");
            }

            runtimes[declared.Key] = new ClaudeCodeRuntime(
                new ClaudeCodeOptions
                {
                    ExecutablePath = declared.ExecutablePath,
                    HealthProbeDirectory = healthProbeDirectory,
                },
                processes());
        }
    }
}
