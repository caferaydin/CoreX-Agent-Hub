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

    [Fact]
    public void The_writes_that_close_a_cancelled_run_are_not_driven_by_the_cancelled_token()
    {
        var code = CodeOnly(Pipeline());

        // The catch exists and is about cancellation the caller asked for, not about failure
        // in general.
        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            code,
            StringComparison.Ordinal);

        // And what it calls closes the run on a bounded token of its own rather than on ct.
        var closing = Between(code, "private async Task<ExecutionRunReport> CloseCancelledAsync", "\n    }");

        Assert.Contains("new MandatoryWorkScope(_options.MandatoryWorkBudget, _clock)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ct)", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ct,", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_never_becomes_a_generic_failure()
    {
        var closing = CodeOnly(Between(
            Pipeline(), "private async Task<ExecutionRunReport> CloseCancelledAsync", "\n    }"));

        Assert.Contains("ExecutionState.Cancelled", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionState.Failed", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void No_unbounded_immunity_was_reintroduced_anywhere_in_orchestration()
    {
        // CancellationToken.None is immunity with no end to it. The bounded scope exists
        // precisely so a stuck write cannot hold a cancelled run open for ever.
        foreach (var file in OrchestrationSources())
        {
            Assert.DoesNotContain("CancellationToken.None", CodeOnly(File.ReadAllText(file)), StringComparison.Ordinal);
        }
    }

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
            Pipeline(), "private async Task<ExecutionRunReport> CloseCancelledAsync", "\n    }"));

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
