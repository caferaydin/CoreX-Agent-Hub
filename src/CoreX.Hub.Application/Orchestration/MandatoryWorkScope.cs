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
///
/// There are two shapes of that idea and they are not the same thing.
///
/// A plain budget starts now and is for work that is already known to be short - disposing
/// a snapshot, confirming a commit.
///
/// <see cref="WhenCallerStops" /> starts nothing until the caller actually cancels. That is
/// the shape the pipeline needs, because the sections it covers are interleaved with real
/// forward work that legitimately takes as long as it takes: a full verification run over a
/// real repository is minutes at least. Starting the countdown when the section begins
/// would make the budget a deadline on that work as well, and every honest long run would
/// die at the bookkeeping call that follows it - which is exactly the defect the first real
/// product acceptance found.
/// </summary>
public sealed class MandatoryWorkScope : IDisposable
{
    private readonly CancellationTokenSource _budget;
    private readonly TimeSpan _allowance;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenRegistration _watch;

    private ITimer? _countdown;
    private int _started;

    /// <summary>A budget that begins immediately. For work already known to be short.</summary>
    public MandatoryWorkScope(TimeSpan budget, TimeProvider? clock = null)
    {
        Ensure(budget);

        _allowance = budget;
        _clock = clock ?? TimeProvider.System;
        _budget = new CancellationTokenSource(budget, _clock);
    }

    private MandatoryWorkScope(TimeSpan budget, CancellationToken caller, TimeProvider? clock)
    {
        Ensure(budget);

        _allowance = budget;
        _clock = clock ?? TimeProvider.System;
        _budget = new CancellationTokenSource();

        // Registering on an already cancelled token runs the callback here and now, so the
        // countdown is started once either way.
        _watch = caller.Register(static state => ((MandatoryWorkScope)state!).Start(), this);
    }

    /// <summary>
    /// A budget that begins the moment <paramref name="caller"/> is cancelled, and never
    /// otherwise.
    ///
    /// While the caller is still waiting there is nothing to be immune from and nothing to
    /// bound: the work is ordinary forward work and takes as long as it takes. The budget is
    /// what a run gets *after* someone has said stop.
    /// </summary>
    public static MandatoryWorkScope WhenCallerStops(
        TimeSpan budget,
        CancellationToken caller,
        TimeProvider? clock = null) => new(budget, caller, clock);

    /// <summary>Independent of the caller's token, and expires on its own.</summary>
    public CancellationToken Token => _budget.Token;

    public void Dispose()
    {
        _watch.Dispose();
        _countdown?.Dispose();
        _budget.Dispose();
    }

    private void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _countdown = _clock.CreateTimer(
            static state => Expire(state), this, _allowance, Timeout.InfiniteTimeSpan);
    }

    private static void Expire(object? state)
    {
        try
        {
            ((MandatoryWorkScope)state!)._budget.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished and let the scope go. Nothing left to bound.
        }
    }

    private static void Ensure(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), budget, "Mandatory work needs a positive budget to run inside.");
        }
    }
}
