using System.Reflection;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>The compiled modules the architecture guards reason about.</summary>
internal static class ArchitectureAssemblies
{
    public static Assembly Domain { get; } = typeof(Domain.Catalog.Organization).Assembly;

    public static Assembly Application { get; } = typeof(Application.Abstractions.IHubStore).Assembly;

    public static Assembly Infrastructure { get; } = typeof(Infrastructure.Persistence.HubDbContext).Assembly;

    public static Assembly Execution { get; } = typeof(Execution.FileSystem.FileSystemGuard).Assembly;

    public static Assembly Workspace { get; } = typeof(global::CoreX.Hub.Workspace.WorkspaceManager).Assembly;

    public static Assembly Verification { get; } = typeof(global::CoreX.Hub.Verification.VerificationEngine).Assembly;

    public static Assembly Agents { get; } = typeof(global::CoreX.Hub.Agents.Abstractions.IAgentRuntime).Assembly;

    public static Assembly ContextModule { get; } =
        typeof(global::CoreX.Hub.Context.ContextPackBuilder).Assembly;

    public static Assembly Cli { get; } = typeof(Cli.HubCli).Assembly;

    /// <summary>Everything, including the composition root.</summary>
    public static IReadOnlyList<Assembly> All { get; } =
        [Domain, Application, Infrastructure, Execution, Workspace, Verification, Agents, ContextModule, Cli];

    /// <summary>
    /// The layers that must stay provider blind. The composition root is excluded on
    /// purpose: wiring a concrete adapter is exactly its job.
    /// </summary>
    public static IReadOnlyList<Assembly> Neutral { get; } =
        [Domain, Application, Infrastructure, Execution, Workspace, Verification, Agents, ContextModule];
}
