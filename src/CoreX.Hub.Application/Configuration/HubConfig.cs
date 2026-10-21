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

    public HubAgentsConfig Agents { get; set; } = new();

    public List<OrganizationConfig> Organizations { get; set; } = [];
}

/// <summary>
/// How the runtimes an agent profile can name are actually built. Configuration, not
/// catalog: nothing here is seeded into the database, and no execution row ever refers to
/// it.
///
/// The catalog says which runtime key a profile uses; this says what stands behind that
/// key on this machine. Keeping the two apart is what lets the same seeded catalog run
/// against a different local installation without a migration.
/// </summary>
public sealed class HubAgentsConfig
{
    /// <summary>
    /// The deterministic offline runtimes available here.
    ///
    /// Named for what they are rather than as a generic runtime list, because a runtime
    /// family needs different facts to be installable and pretending otherwise would invite
    /// a provider switch into configuration.
    /// </summary>
    public List<ScriptedRuntimeConfig> ScriptedRuntimes { get; set; } = [];

    /// <summary>
    /// The installed real runtimes available here, by the same rule as the list above: a
    /// section per family, each stating only what installing that family actually needs.
    ///
    /// What is deliberately not here is as important as what is. No credential, no token,
    /// no endpoint, no account and no billing setting: authentication is the workstation's
    /// business, out of band, and nothing in this file is a place to paste a secret. Nor is
    /// the tool surface, the permission mode or a shell switch here - those are frozen
    /// decisions in the adapter, not deployment settings.
    /// </summary>
    public List<ClaudeCodeRuntimeConfig> ClaudeCodeRuntimes { get; set; } = [];

    /// <summary>
    /// The pinned Codex installations available here.
    ///
    /// Its own section for the same reason the others have one: what a family needs to be
    /// installable differs. This one needs the exact build, because the behaviour a review
    /// depends on has moved between Codex releases in both directions, and the release a
    /// developer machine has on PATH is usually not the accepted one.
    /// </summary>
    public List<CodexRuntimeConfig> CodexRuntimes { get; set; } = [];

    /// <summary>
    /// Every runtime key this installation declares, whatever family declared it.
    ///
    /// The sections above are per family because what a family needs to be installable
    /// differs. The keys are not: a profile names one, and whether it resolves is the same
    /// question for all of them. A caller that only wants that answer asks here rather than
    /// enumerating the families itself - which is what keeps "which runtimes exist" from
    /// becoming a list every new adapter has to remember to extend.
    /// </summary>
    public IReadOnlyList<string> DeclaredRuntimeKeys =>
    [
        .. ScriptedRuntimes.Select(r => r.Key)
            .Concat(ClaudeCodeRuntimes.Select(r => r.Key))
            .Concat(CodexRuntimes.Select(r => r.Key))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];
}

public sealed class ScriptedRuntimeConfig
{
    /// <summary>The <c>runtimeKey</c> an agent profile names to reach this runtime.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Absolute path to the fixture executable it drives. Never discovered on PATH.</summary>
    public string FixturePath { get; set; } = string.Empty;

    /// <summary>
    /// Which filesystem <see cref="FixturePath"/> is a path on: <c>Host</c> or <c>Isolated</c>.
    ///
    /// Stated rather than inferred, because the two cannot be told apart reliably and guessing wrong
    /// is dangerous in both directions. A host path is validated against this machine and is what a
    /// logical organization runs. An isolated path names a file inside a hard organization's own
    /// environment, where this process cannot look - checking it here would resolve
    /// <c>/opt/tool</c> against the current drive, find nothing, and refuse a runtime that exists;
    /// and if something did happen to sit at that Windows path, the value handed to the isolated
    /// workload would be a Windows path for it to execute.
    ///
    /// Host is the default because every declaration that existed before this field was one.
    /// </summary>
    public string FixtureLocation { get; set; } = "Host";

    /// <summary>Which deterministic scenario the fixture plays, by name.</summary>
    public string Mode { get; set; } = string.Empty;
}

/// <summary>
/// One installed real runtime on this machine. Configuration, not catalog: the catalog
/// says which key a profile uses, this says what stands behind that key here.
/// </summary>
public sealed class ClaudeCodeRuntimeConfig
{
    /// <summary>The <c>runtimeKey</c> an agent profile names to reach this runtime.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the installed executable.
    ///
    /// Never a bare name: the child environment is deny by default and nothing is resolved
    /// on PATH, so an operator reading this file can see exactly what will run.
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;
}

/// <summary>
/// One pinned Codex installation on this machine.
///
/// No credential, no token, no endpoint and no account: authentication is the workstation's
/// own Codex session, out of band, and the Hub neither reads nor replaces it.
/// </summary>
public sealed class CodexRuntimeConfig
{
    /// <summary>The <c>runtimeKey</c> an agent profile names to reach this runtime.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the pinned executable.
    ///
    /// Never a bare name. This runtime does not look on PATH, and on a machine where a
    /// newer Codex is installed globally, looking there would silently run the review on a
    /// build that was measured and rejected.
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// The exact version that path must report. Not a floor and not a range: a build nobody
    /// has measured is refused rather than tried.
    /// </summary>
    public string AcceptedVersion { get; set; } = string.Empty;
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

    /// <summary>
    /// Where the hard isolated environment is and what it may consume.
    ///
    /// Required when <see cref="IsolationLevel"/> is Hard and meaningless otherwise. It is
    /// machine local operational configuration - which distribution, which Linux user, which
    /// native paths - so it lives in the configuration file and is deliberately not persisted
    /// to the database: none of it is catalogue truth, and a stale row claiming an environment
    /// exists would be the exact failure the readiness checks exist to prevent.
    /// </summary>
    public HardIsolationConfig? HardIsolation { get; set; }

    public List<string> EnvironmentAllowlist { get; set; } = [];

    public List<AgentProfileConfig> AgentProfiles { get; set; } = [];

    public List<ProjectConfig> Projects { get; set; } = [];
}

/// <summary>
/// How to reach one organization's hard isolated execution environment.
///
/// Provider neutral by construction: there is no field here for a model, a token, an
/// executable or an account. Authentication stays out of band inside the environment, exactly
/// as it does on Windows (ADR-0009); isolation is a question about the operating system
/// boundary and nothing else.
///
/// Every field is consumed by a readiness check or by the future execution plane. Nothing is
/// decorative - a value nobody reads would be a value nobody could be wrong about.
/// </summary>
public sealed class HardIsolationConfig
{
    /// <summary>Which mechanism provides the boundary. Exactly one is supported.</summary>
    public string Backend { get; set; } = string.Empty;

    /// <summary>Names the environment - for WSL2, the distribution.</summary>
    public string EnvironmentId { get; set; } = string.Empty;

    /// <summary>The unprivileged identity the agent runs as inside the environment.</summary>
    public string AgentUser { get; set; } = string.Empty;

    /// <summary>
    /// The trusted identity that owns Hub data inside the environment.
    ///
    /// Distinct from the agent on purpose: the evidence store must be unreadable by the thing
    /// being observed, and an owner who is also the observed cannot provide that.
    /// </summary>
    public string ServiceUser { get; set; } = string.Empty;

    /// <summary>Linux native path for worktrees and execution scratch.</summary>
    public string ExecutionRoot { get; set; } = string.Empty;

    /// <summary>Linux native path for this organization's evidence store.</summary>
    public string EvidenceRoot { get; set; } = string.Empty;

    /// <summary>Finite ceiling on concurrent processes. Maps to TasksMax in Phase 10C-2.</summary>
    public int ProcessLimit { get; set; }

    /// <summary>Finite ceiling on memory. Maps to MemoryMax in Phase 10C-2.</summary>
    public long MemoryLimitBytes { get; set; }

    /// <summary>
    /// Logical verification executable name to absolute path inside this organization's environment.
    ///
    /// The same logical names a verification profile already declares, mapped to where those tools
    /// actually live in this environment. It is per organization rather than global because that is
    /// what it describes: two hard organizations may run different distributions, different
    /// toolchain versions and different installation prefixes, and one map claiming to speak for
    /// both would be wrong about at least one of them.
    ///
    /// Trusted operator configuration. Nothing here comes from a repository, a task, a prompt, an
    /// agent or a verification step's arguments - a step may choose only the logical name, and this
    /// is the only thing that says what that name means.
    ///
    /// There is deliberately no relationship to the host map. A hard organization never falls back
    /// to <see cref="HubVerificationConfig.Executables"/>: those are Windows paths, they mean
    /// nothing inside the environment, and a translation between them would be the Hub guessing.
    /// </summary>
    public Dictionary<string, string> VerificationExecutables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// The agent profile name an independent review of this repository runs under.
    ///
    /// A name, resolved the same way an implementer profile is: profile to runtime key to
    /// registry to runtime. Naming a provider here, or picking whichever profile happens to
    /// have the Reviewer role, would put the choice in code and make swapping the reviewer a
    /// code change rather than a configuration one.
    ///
    /// Absent means this repository is not reviewed, and that is a deliberate statement
    /// rather than a silent skip: a repository that names a profile which cannot review
    /// refuses to start an execution at all.
    /// </summary>
    public string? ReviewerProfile { get; set; }

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
