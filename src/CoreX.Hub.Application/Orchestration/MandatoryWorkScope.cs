namespace CoreX.Hub.Application.Orchestration;

/// <summary>
/// A bounded token for the work a cancelled run still owes.
///
/// Cancellation stops the agent and stops the pipeline. It must not stop the record of what
/// they did: a run whose evidence was discarded because the caller pressed stop is
/// indistinguishable afterwards from a run that did nothing, and the worktree it left
/// behind then has no explanation attached to it.
///
/// The immunity is deliberately narrow. This is not the caller's token ignored, it is a
/// different token with its own budget, so the sections that must finish get exactly as
/// long as closing an execution honestly needs and no longer. A stuck git command cannot
/// hold a cancelled run open for ever by being the thing that was supposed to end it.
/// </summary>
public sealed class MandatoryWorkScope : IDisposable
{
    private readonly CancellationTokenSource _budget;

    public MandatoryWorkScope(TimeSpan budget, TimeProvider? clock = null)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), budget, "Mandatory work needs a positive budget to run inside.");
        }

        _budget = new CancellationTokenSource(budget, clock ?? TimeProvider.System);
    }

    /// <summary>Independent of the caller's token, and expires on its own.</summary>
    public CancellationToken Token => _budget.Token;

    public void Dispose() => _budget.Dispose();
}
