using CoreX.Hub.Composition;
using System.Globalization;
using CoreX.Hub.Composition.Verification;
using CoreX.Hub.Domain.Executions;

// CoreX.Hub.Execution is an assembly namespace, so the domain type of the same short
// name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// Reads what was recorded. Nothing here runs git, an agent or a verification command.
///
/// That restraint is the feature. Evidence is what an execution left behind at the moment
/// it happened, and regenerating a diff from the worktree as it stands now would answer a
/// different question while looking like the same one - the working tree may have moved on,
/// and for a run that failed it is the only copy of what was done.
///
/// Everything shown has already been through redaction on the way in, so there is no raw
/// buffer on this path and deliberately no flag that would ask for one.
/// </summary>
public static class EvidenceCommands
{
    public static async Task<int> ShowAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var target = ParseTarget(args);
        var execution = await ResolveExecutionAsync(session, target, args.GetOption("org"), ct);

        var artifacts = await session.Store.ListEvidenceAsync(execution.Id, ct);
        var blobs = VerificationComposition.CreateEvidenceStore(session.DataDirectory);

        Output.WriteField(output, "Task", target.TaskKey);
        Output.WriteField(output, "Attempt", execution.AttemptNo.ToString(CultureInfo.InvariantCulture));
        Output.WriteField(output, "Execution", execution.Id.ToString("D"));

        Output.WriteField(
            output,
            "Execution state",
            execution.Outcome is { } outcome ? $"{execution.State} ({outcome})" : execution.State.ToString());

        Output.WriteField(output, "Result commit", execution.ResultCommitSha ?? "(none)");
        output.WriteLine();

        if (args.HasFlag("diff"))
        {
            return await WriteDiffAsync(execution, artifacts, blobs, output, ct);
        }

        // Deterministic order: kind, then hash. Two runs of this command over the same
        // execution print the same rows in the same sequence.
        // The step is a column rather than an implication. Since the review became real two
        // producers write the same kinds - the implementer's transcript and the reviewer's are
        // both AgentStdout - and a reader who cannot tell them apart is reading the wrong one
        // half the time.
        var steps = (await session.Store.ListExecutionStepsAsync(execution.Id, ct))
            .ToDictionary(s => s.Id, s => s.Kind.ToString());

        Output.WriteTable(
            output,
            ["KIND", "STEP", "BYTES", "MEDIA TYPE", "REDACTIONS", "SHA256"],
            artifacts
                .OrderBy(a => a.Kind)
                .ThenBy(a => a.Sha256, StringComparer.Ordinal)
                .Select(a => new[]
                {
                    a.Kind.ToString(),
                    a.ExecutionStepId is { } id && steps.TryGetValue(id, out var kind) ? kind : "(execution)",
                    a.SizeBytes.ToString(CultureInfo.InvariantCulture),
                    a.MediaType,
                    a.RedactionCount.ToString(CultureInfo.InvariantCulture),
                    a.Sha256,
                }));

        return ExitCodes.Success;
    }

    /// <summary>
    /// Shows the diff that belongs to this execution.
    ///
    /// Which one that is depends on what the execution produced, and the answer is stated
    /// rather than implied: an execution that made a Hub commit is described by that
    /// commit's diff, and one that made none is described by the working tree it left. An
    /// absent artifact is reported as absent - there is nothing to regenerate it from that
    /// would still be true.
    /// </summary>
    private static async Task<int> WriteDiffAsync(
        DomainExecution execution,
        IReadOnlyList<EvidenceArtifact> artifacts,
        Infrastructure.Evidence.EvidenceBlobStore blobs,
        TextWriter output,
        CancellationToken ct)
    {
        var preferred = execution.ResultCommitSha is null
            ? EvidenceKind.GitWorktreeDiff
            : EvidenceKind.GitCommitDiff;

        var artifact = artifacts
            .Where(a => a.Kind == preferred)
            .OrderByDescending(a => a.CreatedAtUtc)
            .FirstOrDefault();

        if (artifact is null)
        {
            Output.WriteField(output, "Diff", $"{preferred}: not recorded for this execution");
            output.WriteLine();
            output.WriteLine(
                "No diff evidence of that kind was stored. Nothing is regenerated from the current "
                + "working tree, because that would describe the repository now rather than what this "
                + "execution did.");

            return ExitCodes.OperationFailed;
        }

        Output.WriteField(output, "Diff", artifact.Kind.ToString());
        Output.WriteField(output, "Sha256", artifact.Sha256);
        Output.WriteField(output, "Redactions", artifact.RedactionCount.ToString(CultureInfo.InvariantCulture));
        output.WriteLine();

        if (!blobs.Exists(artifact.Sha256))
        {
            output.WriteLine($"blob {artifact.Sha256} is recorded but missing from the evidence store.");
            return ExitCodes.OperationFailed;
        }

        output.WriteLine(await blobs.ReadTextAsync(artifact.Sha256, ct));
        return ExitCodes.Success;
    }

    private static async Task<DomainExecution> ResolveExecutionAsync(
        HubSession session,
        (string TaskKey, int AttemptNo) target,
        string? organizationSlug,
        CancellationToken ct)
    {
        var view = await session.Tasks.GetAsync(target.TaskKey, organizationSlug, ct);
        var executions = await session.Store.ListExecutionsForTaskAsync(view.Task.Id, ct);

        return executions.FirstOrDefault(e => e.AttemptNo == target.AttemptNo)
            ?? throw new UsageException(
                $"Task '{target.TaskKey}' has no attempt {target.AttemptNo}.");
    }

    private static (string TaskKey, int AttemptNo) ParseTarget(ArgParser args)
    {
        var raw = args.PositionalAt(2)
            ?? throw new UsageException("Usage: hub evidence show <TASKKEY>#<attempt> [--diff] [--org <slug>]");

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
}
