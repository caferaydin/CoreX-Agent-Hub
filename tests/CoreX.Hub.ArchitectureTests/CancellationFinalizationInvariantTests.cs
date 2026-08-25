using System.Text.RegularExpressions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What a cancelled run still owes, expressed as shape.
///
/// Behaviour is proved by the orchestration tests, which cancel at each real stage and read
/// the rows back. These guard the two things a behaviour test cannot: that the writes which
/// close a cancelled run are not driven by the token that just cancelled it, and that the
/// escape hatches - an unbounded token, a reconciler call on the normal path - stay absent.
/// </summary>
public sealed class CancellationFinalizationInvariantTests
{
    private static string Pipeline() => Source("ExecutionOrchestrator.Pipeline.cs");

    private static string Close() => Source("ExecutionOrchestrator.Close.cs");

    [Fact]
    public void The_writes_that_close_a_cancelled_run_are_not_driven_by_the_cancelled_token()
    {
        // The catch exists and is about cancellation the caller asked for, not about failure
        // in general.
        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            CodeOnly(Pipeline()),
            StringComparison.Ordinal);

        // And what it calls closes the run on a bounded token of its own rather than on ct.
        var closing = Between(
            CodeOnly(Close()), "private async Task<ExecutionRunReport> CloseCancelledAsync", "\n    }");

        Assert.Contains("new MandatoryWorkScope(_options.MandatoryWorkBudget, _clock)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ct)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ct,", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_never_becomes_a_generic_failure()
    {
        // The body that decides, not the wrapper that opens the scope for it.
        var closing = CodeOnly(Between(
            Close(), "private async Task<ExecutionRunReport> CloseDurablyAsync", "\n    }"));

        Assert.Contains("ExecutionState.Cancelled", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionState.Failed", closing, StringComparison.Ordinal);
    }

    // Unbounded immunity - CancellationToken.None anywhere in orchestration - is guarded by
    // OrchestratorInvariantTests.Cancellation_immunity_is_bounded_rather_than_absolute, which
    // makes the same assertion over a strict superset of these files: it covers the Cli
    // orchestration sources as well, and also proves the bounded alternative exists. A second
    // narrower copy here added no boundary and gave one cause two failures.

    [Fact]
    public void A_cancelled_execution_cannot_carry_a_result_commit()
    {
        // Enforced by the aggregate rather than by the caller, so no orchestration path can
        // produce a row that says "cancelled" over a commit that exists in the repository.
        var execution = Started();

        execution.RecordResultCommit(new string('a', 40), DateTimeOffset.UnixEpoch);

        foreach (var terminal in new[]
                 {
                     ExecutionState.Cancelled, ExecutionState.Failed,
                     ExecutionState.TimedOut, ExecutionState.Interrupted,
                 })
        {
            Assert.Throws<CoreX.Hub.Domain.Common.DomainException>(
                () => execution.Terminate(terminal, DateTimeOffset.UnixEpoch));
        }

        Assert.False(ExecutionStateRules.ProducesResultCommit(ExecutionOutcome.RuntimePolicyViolation));
    }

    [Fact]
    public void Closing_a_cancelled_run_never_reaches_the_agent_or_the_result_commit()
    {
        var closing = CodeOnly(Between(
            Close(), "private async Task<ExecutionRunReport> CloseCancelledAsync", "\n    }"));

        foreach (var forbidden in new[] { "_agent.", "CreateResultCommitAsync", "_verifier.", "_workspace." })
        {
            Assert.DoesNotContain(forbidden, closing, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_reconciler_is_not_called_from_the_normal_cancellation_path()
    {
        // It is a crash fallback. A process that saw the cancellation finishes the run
        // itself, and never asks the repair path to do it - so the orchestrator, which is
        // that path, does not know the reconciler exists. Its own file is not the subject.
        var orchestrator = OrchestrationSources()
            .Where(f => Path.GetFileName(f).StartsWith("ExecutionOrchestrator", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(orchestrator);

        foreach (var file in orchestrator)
        {
            Assert.DoesNotContain(
                nameof(StaleExecutionReconciler),
                CodeOnly(File.ReadAllText(file)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_operator_decision_is_written_only_after_the_execution_has_stopped()
    {
        var service = CodeOnly(Source("TaskCancellationService.cs"));

        // The order is the invariant: ask, observe a terminal execution, then record.
        var request = service.IndexOf("_signal.TryRequest(", StringComparison.Ordinal);
        var observed = service.IndexOf("observed.IsTerminal", StringComparison.Ordinal);
        var confirm = service.IndexOf("ConfirmStoppedAsync(", StringComparison.Ordinal);

        Assert.True(request > 0 && observed > request, "the runner is asked before anything is observed");
        Assert.True(confirm > observed, "the decision is only reached through a terminal execution");

        // Recording is reached from the confirmation, never straight from the request.
        Assert.DoesNotContain("TryRequest(running.Id);\n        return await RecordAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_capture_only_blocks_the_decision_when_an_agent_actually_ran()
    {
        // The distinction that lets an operator cancel a run that never reached the agent:
        // a Pending capture with no agent is "nothing to capture", not "not captured yet".
        var service = CodeOnly(Source("TaskCancellationService.cs"));

        Assert.Contains("agentRan", service, StringComparison.Ordinal);
        Assert.Contains("agentRan && capture is", service, StringComparison.Ordinal);
    }

    [Fact]
    public void The_write_that_claims_an_execution_is_covered_too()
    {
        var pipeline = CodeOnly(Pipeline());

        // The claim is one transaction and the exception it can throw says nothing about
        // whether it committed. Leaving that unguarded left an execution row nobody owned -
        // measured, not imagined - so the start has a catch of its own that asks the database
        // instead of guessing.
        Assert.Contains("start = await PersistStartAsync(", pipeline, StringComparison.Ordinal);
        Assert.Contains("CloseStartedIfDurableAsync(taskId)", pipeline, StringComparison.Ordinal);

        // And when nothing was written there is nothing to account for: the cancellation is
        // rethrown rather than turned into an attempt that never existed.
        Assert.Contains("throw;", pipeline, StringComparison.Ordinal);

        var close = CodeOnly(Close());

        Assert.Contains("ListExecutionsForTaskAsync(taskId, record)", close, StringComparison.Ordinal);
        Assert.Contains("started is null", close, StringComparison.Ordinal);
    }

    [Fact]
    public void The_close_path_reasons_from_the_database_rather_than_from_a_mutated_aggregate()
    {
        var code = CodeOnly(Source("ExecutionOrchestrator.Close.cs"));

        // Identifiers, not aggregates. A transition is a mutation followed by a save, and a
        // save that was cancelled leaves the instance already mutated over a row that is
        // not - so an aggregate crossing into the close would carry an intention that was
        // abandoned, and asking it anything would get that intention back.
        Assert.Contains(
            "private async Task<ExecutionRunReport> CloseCancelledAsync(Guid executionId, Guid taskId)",
            code,
            StringComparison.Ordinal);

        Assert.DoesNotContain("CloseCancelledAsync(Execution ", code, StringComparison.Ordinal);

        // Everything it reads and everything it writes goes through a scope of its own. The
        // orchestrator's own store is the one whose save was interrupted; saving through it
        // would flush the mutations that were given up on.
        Assert.Contains("_detached.Open()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_store", code, StringComparison.Ordinal);

        // The pipeline hands over identifiers too, so nothing tracked is even in scope.
        Assert.Contains(
            "return await CloseCancelledAsync(execution.Id, task.Id);",
            CodeOnly(Pipeline()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminal_execution_is_never_terminalized_a_second_time()
    {
        var code = CodeOnly(Source("ExecutionOrchestrator.Close.cs"));

        // The durable read comes first, and the answer it gives decides everything. Both
        // transitions live behind it.
        var read = code.IndexOf("await store.FindExecutionAsync(executionId, record)", StringComparison.Ordinal);
        var terminalCheck = code.IndexOf("if (execution.IsTerminal)", StringComparison.Ordinal);

        Assert.True(read > 0, "the close must read the execution durably");
        Assert.True(terminalCheck > read, "the terminal check must read what the database says");

        Assert.True(
            code.IndexOf("execution.Terminate(", StringComparison.Ordinal) > terminalCheck,
            "no terminal transition may be attempted before the durable state has been checked");

        Assert.True(
            code.IndexOf("ExecutionCompletion.Apply(", StringComparison.Ordinal) > terminalCheck,
            "no completion may be attempted before the durable state has been checked");
    }

    [Fact]
    public void A_commit_bearing_execution_can_never_be_closed_as_cancelled()
    {
        var code = CodeOnly(Source("ExecutionOrchestrator.Close.cs"));

        // Every terminal error state asserts the attempt produced nothing. A commit is real
        // in the repository whatever the row says, so the commit bearing branch returns
        // before Cancelled is ever named.
        var commitBearing = code.IndexOf(
            "if (execution.ResultCommitSha is not null)", StringComparison.Ordinal);

        var cancelled = code.IndexOf("ExecutionState.Cancelled", StringComparison.Ordinal);

        Assert.True(commitBearing > 0, "the commit bearing branch must exist");
        Assert.True(cancelled > commitBearing, "Cancelled must be unreachable for a commit bearing execution");

        // And how far it actually got is read, not assumed: the two accepted post commit
        // mappings are different answers and one of them would contradict the record.
        Assert.Contains("ExecutionOutcome.ReviewUnusable", code, StringComparison.Ordinal);
        Assert.Contains("ExecutionOutcome.VerificationFailed", code, StringComparison.Ordinal);
        Assert.Contains("ExecutionStepKind.Verify", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_close_is_bounded_and_happens_once()
    {
        var code = CodeOnly(Source("ExecutionOrchestrator.Close.cs"));

        // The same bounded allowance every other mandatory section gets, and no immunity.
        Assert.Contains(
            "new MandatoryWorkScope(_options.MandatoryWorkBudget, _clock)", code, StringComparison.Ordinal);

        Assert.DoesNotContain("CancellationToken.None", code, StringComparison.Ordinal);

        // One path, not a loop: a finalization that could call itself would turn a failed
        // write into an unbounded retry of the thing that failed.
        Assert.Single(
            code.Split('\n'),
            l => l.Contains("CloseCancelledAsync(", StringComparison.Ordinal));

        // A durable truth that cannot be established is reported, never invented.
        Assert.Contains("ExecutionFinalizationException", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_review_ends_the_run_rather_than_escaping_it()
    {
        var code = CodeOnly(Source("ExecutionOrchestrator.Review.cs"));

        // The review is the last external work an execution does, and by the time it runs a
        // result commit exists. Letting cancellation out of here would reach the pipeline's
        // catch, which is written for a run that had not committed yet.
        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            code,
            StringComparison.Ordinal);

        // What it becomes is the absence of a review, which is never a pass.
        var caught = Between(code, "catch (OperationCanceledException)", "await WriteReviewEvidenceAsync");

        Assert.Contains("ReviewVerdict.Unusable", caught, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewVerdict.Clean", caught, StringComparison.Ordinal);

        // And every write that closes the run is driven by the bounded token, not by the one
        // the caller just cancelled. Only the reviewer itself is given ct.
        var closing = Between(code, "await WriteReviewEvidenceAsync", "private async Task WriteReviewEvidenceAsync");

        Assert.DoesNotContain(" ct)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain(" ct,", closing, StringComparison.Ordinal);
        Assert.Contains("record)", closing, StringComparison.Ordinal);
    }

    private static IEnumerable<string> OrchestrationSources() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            && f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

    private static CoreX.Hub.Domain.Executions.Execution Started() =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            new string('b', 40),
            "agent/HUB-001/1",
            "(not created yet)",
            null,
            DateTimeOffset.UnixEpoch);

    private static string Source(string fileName) =>
        File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal)));

    /// <summary>The text of one method, from its signature to the first line that closes it.</summary>
    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' was not found");

        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' was not found after '{from}'");

        return source[start..end];
    }

    private static string CodeOnly(string source) =>
        string.Join(
            '\n',
            source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
