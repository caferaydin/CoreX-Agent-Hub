using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Cli.Commands;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Cli;

/// <summary>
/// Command dispatch. Kept as a class (rather than only a top-level Program) so the
/// integration tests can drive the exact same code path as the shipped executable.
/// </summary>
public static class HubCli
{
    public const string UsageText = """
        CoreX Agent Hub CLI (catalog, tasks, git workspaces)

        usage: hub [global options] <group> <command> [options]

        global options:
          --data-dir <path>    State directory (default: %LOCALAPPDATA%\CoreXAgentHub)
          --config <path>      hub.config.yaml to seed from
          --help               Show this text
          --version            Show the version

        commands:
          hub org list
          hub project list [--org <slug>]
          hub repo list [--org <slug>] [--project <slug>]

          hub task create --org <slug> --project <slug> [--repo <slug>]
                          --objective <text> [--key <TASKKEY>]
                          [--allow <glob>]... [--forbid <glob>]...
                          [--machine-criterion <ref>=<text>]...
                          [--human-criterion <text>]...
                          [--undeclared-criterion <text>]...
                          [--actor <name>]
          hub task show <TASKKEY> [--org <slug>]
          hub task ready <TASKKEY> [--org <slug>] [--actor <name>]

          hub workspace reconcile     --org <slug> --repo <slug>
          hub workspace release       --org <slug> --repo <slug> --path <dir> --confirm
          hub workspace purge-orphans --org <slug> --repo <slug> --confirm

        exit codes:
          0 success   1 usage   2 operation refused   3 ready gate not satisfied   70 unexpected

        Workspace commands never remove anything without --confirm, and an orphaned
        worktree is always reported rather than deleted.

        Later phase commands are intentionally not implemented yet:
        run, accept, request-changes, reject, evidence, doctor.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> argv,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var args = ArgParser.Parse(argv);

            if (args.HasFlag("help") || argv.Count == 0)
            {
                output.WriteLine(UsageText);
                return argv.Count == 0 ? ExitCodes.Usage : ExitCodes.Success;
            }

            if (args.HasFlag("version"))
            {
                output.WriteLine(typeof(HubCli).Assembly.GetName().Version?.ToString() ?? "0.0.0");
                return ExitCodes.Success;
            }

            var group = args.PositionalAt(0);
            var command = args.PositionalAt(1);

            await using var session = await HubSession.OpenAsync(
                args.GetOption("data-dir"),
                args.GetOption("config"),
                error,
                ct);

            if (group == "workspace")
            {
                // Workspace commands drive real git through ProcessRunner, which is
                // Windows only by design (Job Object based process tree termination).
                if (!OperatingSystem.IsWindows())
                {
                    throw new UsageException("Workspace commands require Windows.");
                }

                return await DispatchWorkspaceAsync(session, args, output, command, ct);
            }

            return (group, command) switch
            {
                ("org", "list") => await CatalogCommands.ListOrganizationsAsync(session, output, ct),

                ("project", "list") => await CatalogCommands.ListProjectsAsync(
                    session, args.GetOption("org"), output, ct),

                ("repo", "list") => await CatalogCommands.ListRepositoriesAsync(
                    session, args.GetOption("org"), args.GetOption("project"), output, ct),

                ("task", "create") => await TaskCommands.CreateAsync(session, args, output, ct),
                ("task", "show") => await TaskCommands.ShowAsync(session, args, output, ct),
                ("task", "ready") => await TaskCommands.ReadyAsync(session, args, output, ct),

                _ => throw new UsageException($"Unknown command '{group} {command}'. Run 'hub --help'."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine($"usage error: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (TaskOperationException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            return ExitCodes.OperationFailed;
        }
        catch (HubConfigurationException ex)
        {
            error.WriteLine($"configuration error: {ex.Message}");
            return ExitCodes.OperationFailed;
        }
        catch (DomainException ex)
        {
            error.WriteLine($"domain error: {ex.Message}");
            return ExitCodes.OperationFailed;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> DispatchWorkspaceAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        string? command,
        CancellationToken ct) =>
        command switch
        {
            "reconcile" => await WorkspaceCommands.ReconcileAsync(session, args, output, ct),
            "release" => await WorkspaceCommands.ReleaseAsync(session, args, output, ct),
            "purge-orphans" => await WorkspaceCommands.PurgeOrphansAsync(session, args, output, ct),
            _ => throw new UsageException($"Unknown command 'workspace {command}'. Run 'hub --help'."),
        };
}
