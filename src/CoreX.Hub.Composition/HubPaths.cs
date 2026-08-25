using CoreX.Hub.Infrastructure.Persistence;

namespace CoreX.Hub.Composition;

/// <summary>
/// Where the local-first application keeps its state (ADR-0001, ADR-0013).
///
/// Data directory resolution order:
///   1. <c>--data-dir</c>
///   2. <c>COREX_HUB_DATA_DIR</c>
///   3. <c>%LOCALAPPDATA%\CoreXAgentHub</c> (or <c>~/.local/share/CoreXAgentHub</c>)
/// </summary>
public static class HubPaths
{
    public const string DataDirEnvironmentVariable = "COREX_HUB_DATA_DIR";

    public const string ConfigFileName = "hub.config.yaml";

    public static string ResolveDataDirectory(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(localAppData, "CoreXAgentHub");
    }

    public static string ResolveDatabasePath(string dataDirectory) =>
        Path.Combine(dataDirectory, HubDatabase.DatabaseFileName);

    /// <summary>
    /// Finds <c>hub.config.yaml</c> next to the data directory, in the current directory,
    /// or by walking up towards the repository root.
    /// </summary>
    public static string ResolveConfigPath(string? explicitPath, string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var candidates = new List<string>
        {
            Path.Combine(dataDirectory, ConfigFileName),
        };

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, ConfigFileName));
            directory = directory.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
