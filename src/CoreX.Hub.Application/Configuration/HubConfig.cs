namespace CoreX.Hub.Application.Configuration;

/// <summary>
/// Shape of <c>hub.config.yaml</c> as consumed by the seeder. Phase 1 catalog concepts
/// only; later-phase runtime settings deliberately have no home here yet.
///
/// Plain mutable POCOs with concrete collection types: the YAML deserialiser needs
/// settable members and cannot materialise read-only collection interfaces.
/// </summary>
public sealed class HubConfig
{
    public int SchemaVersion { get; set; } = 1;

    public HubWorkspaceConfig Workspace { get; set; } = new();

    public HubVerificationConfig Verification { get; set; } = new();

    public List<OrganizationConfig> Organizations { get; set; } = [];
}

/// <summary>
/// Machine verification settings that are configuration, not catalog: nothing here is
/// seeded into the database.
/// </summary>
public sealed class HubVerificationConfig
{
    /// <summary>
    /// Logical executable name to absolute executable path. A verification step may only
    /// name a key that appears here (Phase 4 brief section 6), so PATH is never trusted
    /// and no tool is discovered at runtime.
    /// </summary>
    public Dictionary<string, string> Executables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HubWorkspaceConfig
{
    /// <summary>Approved Phase 0 decision: <c>D:\cx</c>.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Approved Phase 0 decision: <c>D:\cx\_fixtures</c>.</summary>
    public string FixtureRoot { get; set; } = string.Empty;
}

public sealed class OrganizationConfig
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string FilesystemRoot { get; set; } = string.Empty;

    public string IsolationLevel { get; set; } = "Logical";

    public bool RequiresHardIsolation { get; set; }

    public List<string> EnvironmentAllowlist { get; set; } = [];

    public List<AgentProfileConfig> AgentProfiles { get; set; } = [];

    public List<ProjectConfig> Projects { get; set; } = [];
}

public sealed class AgentProfileConfig
{
    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string RuntimeKey { get; set; } = string.Empty;

    public string PermissionProfile { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? Effort { get; set; }

    public int SoftTimeoutSeconds { get; set; }

    public int IdleTimeoutSeconds { get; set; }

    public int HardTimeoutSeconds { get; set; }
}

public sealed class ProjectConfig
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TaskKeyPrefix { get; set; } = string.Empty;

    public List<RepositoryConfig> Repositories { get; set; } = [];
}

public sealed class RepositoryConfig
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public string DefaultBranch { get; set; } = "main";

    public List<string> ProtectedBranches { get; set; } = [];

    public List<VerificationProfileConfig> VerificationProfiles { get; set; } = [];
}

public sealed class VerificationProfileConfig
{
    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public string WorkingDirectory { get; set; } = ".";

    public List<VerificationStepConfig> Steps { get; set; } = [];
}

public sealed class VerificationStepConfig
{
    public string Name { get; set; } = string.Empty;

    public string Executable { get; set; } = string.Empty;

    public List<string> Args { get; set; } = [];

    public int TimeoutSeconds { get; set; }

    public bool Required { get; set; } = true;

    public string ResultParser { get; set; } = "none";

    /// <summary>
    /// Where the step writes its machine readable report, relative to the verification
    /// working directory. Required when <see cref="ResultParser"/> is not "none".
    /// </summary>
    public string? ResultPath { get; set; }
}
