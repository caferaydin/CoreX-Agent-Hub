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

    public List<OrganizationConfig> Organizations { get; set; } = [];
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
}
