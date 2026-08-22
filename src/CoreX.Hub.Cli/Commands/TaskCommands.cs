using System.Globalization;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// Phase 1 task commands: create, show, and the Draft -&gt; Ready transition.
/// There is deliberately no run / accept / reject here (phase boundary).
/// </summary>
public static class TaskCommands
{
    public static async Task<int> CreateAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var request = new CreateTaskRequest
        {
            OrganizationSlug = args.RequireOption("org"),
            ProjectSlug = args.RequireOption("project"),
            RepositorySlug = args.GetOption("repo"),
            Objective = args.RequireOption("objective"),
            TaskKey = args.GetOption("key"),
            AllowedScope = args.GetOptions("allow"),
            ForbiddenScope = args.GetOptions("forbid"),
            Criteria = ParseCriteria(args),
            Actor = ResolveActor(args),
        };

        var view = await session.Tasks.CreateAsync(request, DateTimeOffset.UtcNow, ct);
        WriteTaskDetail(view, output);
        return ExitCodes.Success;
    }

    public static async Task<int> ShowAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var key = args.PositionalAt(2)
            ?? throw new UsageException("Usage: hub task show <TASKKEY> [--org <slug>]");

        var view = await session.Tasks.GetAsync(key, args.GetOption("org"), ct);
        WriteTaskDetail(view, output);
        return ExitCodes.Success;
    }

    public static async Task<int> ReadyAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var key = args.PositionalAt(2)
            ?? throw new UsageException("Usage: hub task ready <TASKKEY> [--org <slug>]");

        var result = await session.Tasks.MarkReadyAsync(
            key,
            args.GetOption("org"),
            ResolveActor(args),
            DateTimeOffset.UtcNow,
            ct);

        WriteTaskDetail(result.View, output);

        if (!result.View.ReadyGate.IsSatisfied)
        {
            output.WriteLine();
            output.WriteLine("ready gate NOT satisfied; task remains in Draft.");
            return ExitCodes.ReadyGateNotSatisfied;
        }

        output.WriteLine();
        output.WriteLine(result.Transitioned
            ? $"{result.View.Task.TaskKey}: Draft -> Ready"
            : $"{result.View.Task.TaskKey}: already Ready");

        return ExitCodes.Success;
    }

    private static IReadOnlyList<CriterionInput> ParseCriteria(ArgParser args)
    {
        var criteria = new List<CriterionInput>();

        foreach (var raw in args.GetOptions("machine-criterion"))
        {
            var separator = raw.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new UsageException(
                    "--machine-criterion expects <verificationRef>=<text>, for example test=\"all unit tests pass\".");
            }

            criteria.Add(new CriterionInput(
                VerificationKind.Machine,
                raw[(separator + 1)..],
                VerificationRef: raw[..separator]));
        }

        foreach (var raw in args.GetOptions("human-criterion"))
        {
            criteria.Add(new CriterionInput(VerificationKind.Human, raw));
        }

        // Present so that the rejected-by-design case (ADR-0015) can be exercised end to end.
        foreach (var raw in args.GetOptions("undeclared-criterion"))
        {
            criteria.Add(new CriterionInput(Kind: null, raw));
        }

        return criteria;
    }

    private static string ResolveActor(ArgParser args) =>
        args.GetOption("actor")
        ?? Environment.UserName
        ?? "unknown";

    private static void WriteTaskDetail(TaskView view, TextWriter output)
    {
        var task = view.Task;

        Output.WriteField(output, "Task", task.TaskKey.Value);
        Output.WriteField(output, "Organization", view.Organization.Slug.Value);
        Output.WriteField(output, "Project", view.Project.Slug.Value);
        Output.WriteField(output, "Repository", view.Repository.Slug.Value);
        Output.WriteField(output, "State", task.State.ToString());
        Output.WriteField(output, "Version", task.Version.ToString(CultureInfo.InvariantCulture));
        Output.WriteField(output, "Objective", task.Objective);
        Output.WriteField(output, "AllowedScope", string.Join(" ", task.AllowedScope));
        Output.WriteField(output, "ForbiddenScope", string.Join(" ", task.ForbiddenScope));
        Output.WriteField(output, "VerifyProfile", view.VerificationProfile?.Name ?? "(none)");

        if (task.BlockReason is { } blockReason)
        {
            Output.WriteField(output, "BlockReason", blockReason);
        }

        output.WriteLine();
        output.WriteLine("Acceptance criteria");

        var rows = task.AcceptanceCriteria.Select(c => new[]
        {
            c.Order.ToString(CultureInfo.InvariantCulture),
            c.Kind?.ToString().ToUpperInvariant() ?? "(undeclared)",
            c.VerificationRef ?? "-",
            c.Text,
        });

        Output.WriteTable(output, ["#", "KIND", "VERIFY-REF", "TEXT"], rows);

        output.WriteLine();
        output.WriteLine($"Ready gate: {(view.ReadyGate.IsSatisfied ? "SATISFIED" : "NOT SATISFIED")}");

        foreach (var violation in view.ReadyGate.Violations)
        {
            output.WriteLine($"  violation  {violation}");
        }

        foreach (var warning in view.ReadyGate.Warnings)
        {
            output.WriteLine($"  warning    {warning}");
        }
    }
}
