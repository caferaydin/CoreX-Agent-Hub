using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Verification;

/// <summary>Why a verification reference could not be resolved to exactly one command.</summary>
public enum MachineTruthProblem
{
    /// <summary>Nothing was recorded under that name for this execution.</summary>
    NotRecorded,

    /// <summary>More than one row claims the name, so no single answer is trustworthy.</summary>
    Ambiguous,
}

/// <summary>
/// The answer to "what did this execution's step called X do", or the reason there is none.
/// </summary>
public sealed record MachineTruthLookup
{
    public CommandResult? Command { get; private init; }

    public MachineTruthProblem? Problem { get; private init; }

    public string Detail { get; private init; } = string.Empty;

    public bool Found => Command is not null;

    public static MachineTruthLookup Resolved(CommandResult command) =>
        new() { Command = command, Detail = $"{command.StepName} = {command.Status}" };

    public static MachineTruthLookup Missing(string detail) =>
        new() { Problem = MachineTruthProblem.NotRecorded, Detail = detail };

    public static MachineTruthLookup Ambiguous(string detail) =>
        new() { Problem = MachineTruthProblem.Ambiguous, Detail = detail };
}

/// <summary>
/// Reads what an execution's verification actually did, from the database and nowhere else.
///
/// This is the seam a Machine acceptance criterion is answered through, and the reason it
/// exists is that the answer is needed in a different process from the one that produced
/// it. So there is no cache here, no run object, and nothing derived from a message written
/// for a person to read: a step name goes in and a stored row comes back.
///
/// It resolves and it reports; it does not decide. Turning a status into an acceptance
/// verdict is somebody else's job, and keeping that out of here is what stops the status
/// from being quietly reduced to a boolean on the way past.
/// </summary>
public sealed class VerificationTruthReader
{
    private readonly IHubStore _store;

    public VerificationTruthReader(IHubStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Every verification command this execution ran, in the order it ran them.</summary>
    public Task<IReadOnlyList<CommandResult>> ListAsync(Guid executionId, CancellationToken ct) =>
        _store.ListVerificationCommandsAsync(executionId, ct);

    /// <summary>
    /// Resolves one verification reference against one execution.
    ///
    /// Exactly one match is an answer. None is an absence, and more than one is a
    /// contradiction - neither is resolved by picking a row, so neither is. A reader that
    /// guessed here would be inventing the machine truth it was asked to report.
    /// </summary>
    public async Task<MachineTruthLookup> ResolveAsync(
        Guid executionId,
        string? verificationRef,
        CancellationToken ct)
    {
        var name = (verificationRef ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return MachineTruthLookup.Missing("the criterion names no verification step");
        }

        var commands = await _store.ListVerificationCommandsAsync(executionId, ct);

        var matches = commands
            .Where(c => string.Equals(c.StepName, name, StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            1 => MachineTruthLookup.Resolved(matches[0]),
            0 => MachineTruthLookup.Missing(
                $"execution {executionId} recorded no verification step named '{name}'"),
            _ => MachineTruthLookup.Ambiguous(
                $"execution {executionId} recorded {matches.Count} verification steps named '{name}'"),
        };
    }
}
