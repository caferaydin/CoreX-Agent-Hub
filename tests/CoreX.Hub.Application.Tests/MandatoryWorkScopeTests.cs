using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// What the work a cancelled run still owes is allowed to cost.
///
/// Two properties, and the whole point is that they differ. While the caller is still
/// waiting there is nothing to be immune from, so there is nothing to bound either: the
/// sections run alongside real forward work - a verification over a real repository is
/// minutes at least - and a budget that had already started would be a deadline on that
/// work as well. Once someone has said stop, the record the run owes gets a fixed
/// allowance and no more.
///
/// The first property is not hypothetical. The first real product acceptance died at the
/// bookkeeping call after an eighty five minute verification, on a two minute budget that
/// had begun before the verification did.
///
/// The budgets here are small and real rather than mocked: the clock the suite uses moves
/// only <c>GetUtcNow</c>, so a scheduled countdown would not move with it, and a proof that
/// depended on that would be proving nothing.
/// </summary>
public sealed class MandatoryWorkScopeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WellPastBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongerThanBudget = TimeSpan.FromMilliseconds(900);

    [Fact]
    public async Task A_caller_that_has_not_cancelled_imposes_no_deadline_at_all()
    {
        using var caller = new CancellationTokenSource();
        using var scope = MandatoryWorkScope.WhenCallerStops(Budget, caller.Token);

        await Task.Delay(LongerThanBudget);

        Assert.False(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Once_the_caller_stops_the_record_gets_the_budget_and_no_more()
    {
        using var caller = new CancellationTokenSource();
        using var scope = MandatoryWorkScope.WhenCallerStops(Budget, caller.Token);

        // Long past the budget, and still nothing: the countdown had not begun.
        await Task.Delay(LongerThanBudget);
        Assert.False(scope.Token.IsCancellationRequested);

        caller.Cancel();

        Assert.True(
            scope.Token.WaitHandle.WaitOne(WellPastBudget),
            "the budget must start when the caller stops");
    }

    [Fact]
    public void A_caller_already_cancelled_still_gets_its_allowance()
    {
        // Immunity is the point: a run cancelled before this section began still owes an
        // account of itself, so it starts the budget rather than arriving with none.
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        using var scope = MandatoryWorkScope.WhenCallerStops(Budget, caller.Token);

        Assert.False(scope.Token.IsCancellationRequested);
        Assert.True(scope.Token.WaitHandle.WaitOne(WellPastBudget));
    }

    [Fact]
    public void The_plain_budget_still_starts_immediately()
    {
        // The other shape, for work already known to be short - disposing a snapshot,
        // confirming a commit - is unchanged.
        using var scope = new MandatoryWorkScope(Budget);

        Assert.False(scope.Token.IsCancellationRequested);
        Assert.True(scope.Token.WaitHandle.WaitOne(WellPastBudget));
    }

    [Fact]
    public void Neither_shape_accepts_a_budget_that_is_not_a_budget()
    {
        using var caller = new CancellationTokenSource();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MandatoryWorkScope(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MandatoryWorkScope.WhenCallerStops(TimeSpan.Zero, caller.Token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MandatoryWorkScope.WhenCallerStops(TimeSpan.FromSeconds(-1), caller.Token));
    }

    [Fact]
    public async Task Disposing_while_a_countdown_is_armed_is_not_an_error()
    {
        // The ordinary path: the run finishes, the scope goes away, and a countdown that was
        // already armed has nothing left to cancel.
        using var caller = new CancellationTokenSource();

        var scope = MandatoryWorkScope.WhenCallerStops(Budget, caller.Token);
        caller.Cancel();
        scope.Dispose();

        await Task.Delay(LongerThanBudget);
    }
}
