using CoreX.Hub.Composition;
using System.Runtime.Versioning;
using CoreX.Hub.Composition.Workspace;
using CoreX.Hub.Infrastructure.Configuration;
using CoreX.Hub.Workspace;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// Phase 3 operator commands.
///
/// Bounded shape: the orchestrator that creates executions is Phase 5, so there is no
/// fake execution engine here. Known workspaces come from whatever executions are already
/// persisted (none until Phase 5), and everything else is discovered from git truth.
/// Nothing is ever removed without an explicit confirmation flag.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WorkspaceCommands
{
    public static async Task<int> ReconcileAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var context = await ResolveAsync(session, args, ct);

        var report = await context.Manager.ReconcileAsync(context.RepositoryPath, context.Known, ct);

        var rows = report.Entries.Select(e => new[]
        {
            e.Health.ToString(),
            e.BranchName ?? "-",
            e.HeadSha is { Length: >= 8 } sha ? sha[..8] : "-",
            e.WorktreePath,
        });

        Output.WriteTable(output, ["HEALTH", "BRANCH", "HEAD", "PATH"], rows);

        output.WriteLine();
        output.WriteLine(
            $"registered: {context.Known.Count} · orphaned: {report.Orphaned.Count} · missing: {report.Missing.Count}");

        if (report.Orphaned.Count > 0)
        {
            output.WriteLine("orphaned worktrees are reported only; nothing is removed automatically.");
        }

        return ExitCodes.Success;
    }

    public static async Task<int> ReleaseAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var context = await ResolveAsync(session, args, ct);
        var path = args.RequireOption("path");
        var confirmed = args.HasFlag("confirm");

        var known = context.Known.FirstOrDefault(k =>
            string.Equals(
                k.WorktreePath.TrimEnd(Path.DirectorySeparatorChar),
                path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

        var workspace = new PreparedWorkspace
        {
            RepositoryPath = context.RepositoryPath,
            WorkspacePath = path,
            BranchName = known?.BranchName ?? "(unknown)",
            BaseCommitSha = new string('0', 40),
            HooksDirectory = context.Manager.Hooks.HooksDirectory,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = await context.Manager.ReleaseAsync(
            workspace,
            hasResultCommit: known?.HasResultCommit ?? false,
            operatorConfirmed: confirmed,
            ct);

        Output.WriteField(output, "Workspace", path);
        Output.WriteField(output, "Result", result.State.ToString());
        Output.WriteField(output, "Attempts", result.RemovalAttempts.ToString(null as IFormatProvider));

        if (result.Detail is { } detail)
        {
            Output.WriteField(output, "Detail", detail);
        }

        return result.State == WorkspaceReleaseState.Released ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    public static async Task<int> PurgeOrphansAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var context = await ResolveAsync(session, args, ct);
        var report = await context.Manager.ReconcileAsync(context.RepositoryPath, context.Known, ct);

        if (!args.HasFlag("confirm"))
        {
            Output.WriteTable(
                output,
                ["ORPHANED"],
                report.Orphaned.Select(o => new[] { o.WorktreePath }));

            output.WriteLine();
            output.WriteLine("nothing was removed; re-run with --confirm to remove these worktrees.");
            return ExitCodes.Usage;
        }

        var failures = 0;

        foreach (var orphan in report.Orphaned)
        {
            var workspace = new PreparedWorkspace
            {
                RepositoryPath = context.RepositoryPath,
                WorkspacePath = orphan.WorktreePath,
                BranchName = orphan.BranchName ?? "(unknown)",
                BaseCommitSha = new string('0', 40),
                HooksDirectory = context.Manager.Hooks.HooksDirectory,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            var result = await context.Manager.ReleaseAsync(
                workspace, hasResultCommit: false, operatorConfirmed: true, ct);

            output.WriteLine($"{result.State,-24}{orphan.WorktreePath}");

            if (result.State != WorkspaceReleaseState.Released)
            {
                failures++;
            }
        }

        return failures == 0 ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    private static async Task<WorkspaceCommandContext> ResolveAsync(
        HubSession session,
        ArgParser args,
        CancellationToken ct)
    {
        var organizationSlug = args.RequireOption("org");
        var repositorySlug = args.RequireOption("repo");

        var organization = await session.Store.FindOrganizationBySlugAsync(organizationSlug, ct)
            ?? throw new UsageException($"Organization '{organizationSlug}' was not found.");

        session.Scope.ScopeTo(organization.Id);

        var repositories = await session.Store.ListRepositoriesAsync(organization.Id, null, ct);
        var repository = repositories.FirstOrDefault(r => r.Slug.Value == repositorySlug)
            ?? throw new UsageException($"Repository '{repositorySlug}' was not found in '{organizationSlug}'.");

        if (!GitToolchain.TryResolve(out var gitPath, out var diagnostic))
        {
            throw new UsageException(diagnostic);
        }

        var config = new YamlHubConfigReader().Read(session.ConfigPath);
        var workspaceRoot = config.Workspace.Root;
        var hubControlRoot = Path.Combine(session.DataDirectory, "git-control");
        Directory.CreateDirectory(hubControlRoot);

        var executor = new ProcessGitCommandExecutor(
            gitPath!,
            [organization.FilesystemRoot, workspaceRoot, hubControlRoot]);

        var manager = new WorkspaceManager(
            executor,
            new FileSystemGuardAdapter(),
            new WorkspaceOptions { HubControlRoot = hubControlRoot });

        var known = await session.Db.Executions
            .Where(e => e.OrganizationId == organization.Id && e.WorktreePath != null)
            .Select(e => new { e.WorktreePath, e.BranchName, e.ResultCommitSha })
            .ToListAsync(ct);

        return new WorkspaceCommandContext(
            repository.LocalPath,
            manager,
            [.. known.Select(k => new KnownWorkspace(k.WorktreePath, k.BranchName, k.ResultCommitSha is not null))]);
    }

    private sealed record WorkspaceCommandContext(
        string RepositoryPath,
        WorkspaceManager Manager,
        IReadOnlyList<KnownWorkspace> Known);
}
