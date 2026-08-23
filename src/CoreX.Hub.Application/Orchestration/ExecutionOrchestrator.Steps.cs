using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Orchestration;

public sealed partial class ExecutionOrchestrator
{
    private async Task AdvanceAsync(
        Execution execution,
        ExecutionState next,
        ExecutionStepKind? startStep,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        execution.Advance(next, now);

        if (startStep is { } kind)
        {
            var step = await FindStepAsync(execution.Id, kind, ct);
            step?.Start(now);
        }

        _store.AddOrchestrationEvent(EventFor(execution, next.ToString(), "entered " + next));
        _store.AddAuditEvent(AuditFor(execution, "ExecutionState", next.ToString()));
        await _store.SaveChangesAsync(ct);
    }

    private async Task FinishStepAsync(
        Guid executionId,
        ExecutionStepKind kind,
        StepFate fate,
        string? reason,
        CancellationToken ct)
    {
        var step = await FindStepAsync(executionId, kind, ct);
        if (step is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();

        switch (fate)
        {
            case StepFate.Succeeded:
                step.Succeed(now);
                break;
            case StepFate.TimedOut:
                step.Timeout(reason ?? "the step exceeded its deadline", now);
                break;
            case StepFate.Cancelled:
                step.Cancel(reason ?? "the run was cancelled", now);
                break;
            default:
                step.Fail(reason ?? "the step failed", now);
                break;
        }

        await _store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Closes the Verify step and writes what its commands did, in one transaction.
    ///
    /// Grouped deliberately. The step's state is the claim "verification happened and this
    /// is how it went"; the command rows are the only durable evidence for that claim, and
    /// they are what a Machine acceptance criterion will be answered from in some later
    /// process. Saving them separately would allow a crash to leave an execution asserting a
    /// verification whose result nobody can look up any more.
    ///
    /// The verification itself has already run by the time this is called: no transaction is
    /// ever open across a subprocess.
    /// </summary>
    private async Task FinishVerifyStepAsync(
        Execution execution,
        StepFate fate,
        string? reason,
        IReadOnlyList<VerificationStepView> commands,
        CancellationToken ct)
    {
        var step = await FindStepAsync(execution.Id, ExecutionStepKind.Verify, ct);
        if (step is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var order = 0;

        foreach (var command in commands)
        {
            _store.AddCommandResult(new CommandResult(
                Guid.CreateVersion7(),
                execution.OrganizationId,
                step.Id,
                ++order,
                command.StepName,
                command.Executable,
                command.Arguments,
                command.Status,
                command.Required,
                command.ExitCode,
                command.DurationMilliseconds,
                command.TestsPassed,
                command.TestsFailed,
                command.TestsSkipped,
                now));
        }

        switch (fate)
        {
            case StepFate.Succeeded:
                step.Succeed(now);
                break;
            case StepFate.Cancelled:
                step.Cancel(reason ?? "the run was cancelled", now);
                break;
            default:
                step.Fail(reason ?? "the step failed", now);
                break;
        }

        await _store.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records a step that deliberately did not run, with the reason. A silent skip would
    /// be indistinguishable from a success nobody checked.
    /// </summary>
    private async Task SkipAsync(Guid executionId, ExecutionStepKind kind, string reason, CancellationToken ct)
    {
        var step = await FindStepAsync(executionId, kind, ct);
        if (step is null)
        {
            return;
        }

        step.Skip(reason, _clock.GetUtcNow());
        await _store.SaveChangesAsync(ct);
    }

    private async Task<ExecutionStep?> FindStepAsync(Guid executionId, ExecutionStepKind kind, CancellationToken ct)
    {
        var steps = await _store.ListExecutionStepsAsync(executionId, ct);
        return steps.FirstOrDefault(s => s.Kind == kind);
    }

    private async Task RecordEventAsync(Execution execution, string eventType, string message, CancellationToken ct)
    {
        _store.AddOrchestrationEvent(EventFor(execution, eventType, message));
        await _store.SaveChangesAsync(ct);
    }

    private OrchestrationEvent EventFor(Execution execution, string eventType, string message) =>
        new(
            Guid.CreateVersion7(),
            execution.OrganizationId,
            execution.Id,
            eventType,
            Bounded(message),
            _clock.GetUtcNow());

    /// <summary>The timeline carries summaries. Content belongs to the evidence store.</summary>
    private static string Bounded(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? "(none)"
            : message.Length <= 1000 ? message : message[..1000];

    private static StepFate StepFateFor(string agentOutcome) => agentOutcome switch
    {
        "Completed" => StepFate.Succeeded,
        "TimedOut" => StepFate.TimedOut,
        "Cancelled" => StepFate.Cancelled,
        _ => StepFate.Failed,
    };

    /// <summary>
    /// K0: what the runtime itself did decides whether the pipeline may continue. A null
    /// here means the agent finished, and only then do the captured change gates matter.
    /// </summary>
    private static ExecutionState? TerminalStateFor(string agentOutcome) => agentOutcome switch
    {
        "Completed" => null,
        "TimedOut" => ExecutionState.TimedOut,
        "Cancelled" => ExecutionState.Cancelled,
        _ => ExecutionState.Failed,
    };
}
