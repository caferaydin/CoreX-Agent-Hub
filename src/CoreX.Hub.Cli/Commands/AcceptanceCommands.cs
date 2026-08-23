using System.Globalization;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// The human decision commands: accept, send back, reject.
///
/// The console lives here and nowhere deeper. This layer shows a person what the machine
/// found, asks them the questions the task declared, and hands the answers to the
/// application as data - which is why the same decision can be made by a test with no
/// terminal attached.
///
/// Machine results are printed and never asked about. There is no flag anywhere below that
/// could state one.
/// </summary>
public static class AcceptanceCommands
{
    public static async Task<int> AcceptAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        IConsoleInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        var target = ParseTarget(args, "accept");
        var organization = args.GetOption("org");

        // Everything is read and every question answered before anything is written: no
        // database transaction is ever open while somebody is typing.
        var preview = await session.Acceptance.PreviewAsync(target.TaskKey, target.AttemptNo, organization, ct);

        WritePreview(preview, output);

        var verdicts = CollectVerdicts(preview, args, output, input);

        var result = await session.Acceptance.AcceptAsync(
            new AcceptanceRequest
            {
                TaskKey = target.TaskKey,
                AttemptNo = target.AttemptNo,
                OrganizationSlug = organization,
                Actor = ResolveActor(args),
                UseOverride = args.HasFlag("override"),
                Reason = args.GetOption("reason"),
                HumanVerdicts = verdicts,
            },
            DateTimeOffset.UtcNow,
            ct);

        WriteResult(result, output);
        return ExitCodes.Success;
    }

    public static async Task<int> RequestChangesAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var result = await session.Acceptance.RequestChangesAsync(
            Decision(args, "request-changes"), DateTimeOffset.UtcNow, ct);

        WriteResult(result, output);
        return ExitCodes.Success;
    }

    public static async Task<int> RejectAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        var result = await session.Acceptance.RejectAsync(
            Decision(args, "reject"), DateTimeOffset.UtcNow, ct);

        WriteResult(result, output);
        return ExitCodes.Success;
    }

    private static TaskDecisionRequest Decision(ArgParser args, string command)
    {
        ArgumentNullException.ThrowIfNull(args);

        var target = ParseTarget(args, command);

        return new TaskDecisionRequest
        {
            TaskKey = target.TaskKey,
            AttemptNo = target.AttemptNo,
            OrganizationSlug = args.GetOption("org"),
            Actor = ResolveActor(args),
            Reason = args.GetOption("reason"),
        };
    }

    /// <summary>
    /// Reads <c>HUB-001#1</c>.
    ///
    /// The attempt is required rather than defaulted to the newest, because a decision is
    /// about a particular piece of work and the person making it should say which.
    /// </summary>
    internal static (string TaskKey, int AttemptNo) ParseTarget(ArgParser args, string command)
    {
        var raw = args.PositionalAt(1)
            ?? throw new UsageException($"Usage: hub {command} <TASKKEY>#<attempt> [--org <slug>]");

        var hash = raw.IndexOf('#', StringComparison.Ordinal);

        if (hash <= 0 || hash == raw.Length - 1)
        {
            throw new UsageException(
                $"'{raw}' does not name an execution. Expected <TASKKEY>#<attempt>, for example HUB-001#1.");
        }

        var attemptText = raw[(hash + 1)..];

        if (!int.TryParse(attemptText, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            || attempt <= 0)
        {
            throw new UsageException($"'{attemptText}' is not an attempt number; attempts start at 1.");
        }

        return (raw[..hash], attempt);
    }

    /// <summary>
    /// Asks the person each Human criterion once, in the order the task declares them.
    ///
    /// Answers may also arrive as <c>--verdict &lt;order&gt;=&lt;verdict&gt;[:note]</c>, so a
    /// decision can be scripted without a terminal. Nothing is assumed either way: an answer
    /// that is not given is not defaulted, it is asked for again.
    /// </summary>
    private static IReadOnlyList<HumanCriterionVerdictRecord> CollectVerdicts(
        AcceptancePreview preview,
        ArgParser args,
        TextWriter output,
        IConsoleInput input)
    {
        var supplied = ParseSuppliedVerdicts(args, preview);
        var verdicts = new List<HumanCriterionVerdictRecord>();

        foreach (var criterion in preview.HumanCriteria)
        {
            if (supplied.TryGetValue(criterion.CriterionId, out var given))
            {
                verdicts.Add(given);
                continue;
            }

            verdicts.Add(Ask(criterion, output, input));
        }

        return verdicts;
    }

    private static HumanCriterionVerdictRecord Ask(
        HumanCriterionPrompt criterion,
        TextWriter output,
        IConsoleInput input)
    {
        output.WriteLine();
        output.WriteLine($"criterion {criterion.Order}: {criterion.Text}");

        if (criterion.HumanCheckHint is { Length: > 0 } hint)
        {
            output.WriteLine($"  how to check: {hint}");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            output.Write("  verdict [met/notmet/na] (optional ':note'): ");

            var line = input.ReadLine();

            if (line is null)
            {
                throw new UsageException(
                    $"No verdict was given for criterion {criterion.Order}. Supply --verdict "
                    + $"{criterion.Order}=met|notmet|na, or answer the prompt.");
            }

            if (TryReadVerdict(line, out var verdict, out var note))
            {
                return new HumanCriterionVerdictRecord(criterion.CriterionId, verdict, note);
            }

            output.WriteLine("  expected met, notmet or na.");
        }

        throw new UsageException($"Criterion {criterion.Order} was not answered.");
    }

    private static Dictionary<Guid, HumanCriterionVerdictRecord> ParseSuppliedVerdicts(
        ArgParser args,
        AcceptancePreview preview)
    {
        var byOrder = preview.HumanCriteria.ToDictionary(
            c => c.Order.ToString(CultureInfo.InvariantCulture),
            StringComparer.Ordinal);

        var supplied = new Dictionary<Guid, HumanCriterionVerdictRecord>();

        foreach (var option in args.GetOptions("verdict"))
        {
            var separator = option.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                throw new UsageException(
                    $"'--verdict {option}' is not <order>=<met|notmet|na>[:note].");
            }

            var order = option[..separator].Trim();

            if (!byOrder.TryGetValue(order, out var criterion))
            {
                throw new UsageException($"There is no human criterion {order} on this task.");
            }

            if (!TryReadVerdict(option[(separator + 1)..], out var verdict, out var note))
            {
                throw new UsageException($"'{option}' does not name a verdict (met, notmet or na).");
            }

            if (!supplied.TryAdd(criterion.CriterionId, new HumanCriterionVerdictRecord(criterion.CriterionId, verdict, note)))
            {
                throw new UsageException($"Human criterion {order} was answered more than once.");
            }
        }

        return supplied;
    }

    /// <summary>
    /// Reads one answer. The three verdicts are the frozen vocabulary and are taken exactly
    /// as given: "not met" is a valid answer, not a failure to answer.
    /// </summary>
    private static bool TryReadVerdict(string text, out HumanCriterionVerdict verdict, out string? note)
    {
        verdict = HumanCriterionVerdict.Met;
        note = null;

        var trimmed = (text ?? string.Empty).Trim();
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);

        if (colon >= 0)
        {
            note = trimmed[(colon + 1)..].Trim();
            note = note.Length == 0 ? null : note;
            trimmed = trimmed[..colon].Trim();
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "met":
            case "yes":
                verdict = HumanCriterionVerdict.Met;
                return true;

            case "notmet":
            case "no":
                verdict = HumanCriterionVerdict.NotMet;
                return true;

            case "na":
            case "n/a":
            case "notapplicable":
                verdict = HumanCriterionVerdict.NotApplicable;
                return true;

            default:
                return false;
        }
    }

    private static void WritePreview(AcceptancePreview preview, TextWriter output)
    {
        Output.WriteField(output, "Task", preview.TaskKey);
        Output.WriteField(output, "Task state", preview.TaskState.ToString());
        Output.WriteField(output, "Attempt", preview.AttemptNo.ToString(CultureInfo.InvariantCulture));
        Output.WriteField(output, "Execution", preview.ExecutionId.ToString("D"));

        Output.WriteField(
            output,
            "Execution state",
            preview.ExecutionOutcome is { } outcome
                ? $"{preview.ExecutionState} ({outcome})"
                : preview.ExecutionState.ToString());

        Output.WriteField(output, "Result commit", preview.ResultCommitSha ?? "(none)");

        if (!preview.IsLatestExecution)
        {
            output.WriteLine("note: this is not the latest attempt.");
        }

        if (preview.ExistingDecision is { } already)
        {
            output.WriteLine($"note: this attempt already has a recorded decision ({already}).");
        }

        output.WriteLine();
        output.WriteLine("machine criteria (read-only, from recorded evidence):");

        Output.WriteTable(
            output,
            ["#", "REF", "VERDICT", "STATUS", "REQUIRED", "CRITERION"],
            preview.MachineCriteria.Select(m => new[]
            {
                m.Order.ToString(CultureInfo.InvariantCulture),
                m.VerificationRef,
                m.Verdict.ToString(),
                m.VerificationStatus.ToString(),
                m.Required ? "yes" : "no",
                m.Text,
            }));

        foreach (var problem in preview.MachineTruthProblems)
        {
            output.WriteLine($"machine truth unavailable: {problem}");
        }
    }

    private static void WriteResult(AcceptanceResult result, TextWriter output)
    {
        output.WriteLine();
        Output.WriteField(output, "Task", result.TaskKey);
        Output.WriteField(output, "Attempt", result.AttemptNo.ToString(CultureInfo.InvariantCulture));
        Output.WriteField(output, "Execution", result.ExecutionId.ToString("D"));
        Output.WriteField(output, "Decision", result.Decision.ToString());

        if (result.OverrideUsed)
        {
            Output.WriteField(output, "Override", "yes (review blocked)");
        }

        Output.WriteField(output, "Task state", result.TaskState.ToString());

        Output.WriteField(
            output,
            "Execution",
            result.ExecutionOutcome is { } outcome
                ? $"{result.ExecutionState} ({outcome}) - unchanged"
                : $"{result.ExecutionState} - unchanged");

        if (result.Reason is { Length: > 0 } reason)
        {
            Output.WriteField(output, "Reason", reason);
        }

        // Reported separately, and after the decision, because it is a different kind of
        // fact: the decision above is recorded whatever this line says.
        Output.WriteField(
            output,
            "Workspace cleanup",
            $"{result.WorkspaceCleanup.Outcome} - {result.WorkspaceCleanup.Detail}");

        foreach (var verdict in result.HumanVerdicts)
        {
            output.WriteLine($"human verdict: {verdict.Verdict}"
                             + (verdict.Note is { Length: > 0 } note ? $" ({note})" : string.Empty));
        }
    }

    private static string ResolveActor(ArgParser args) =>
        args.GetOption("actor") ?? Environment.UserName ?? "unknown";
}
