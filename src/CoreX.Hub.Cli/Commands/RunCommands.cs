using CoreX.Hub.Composition;
using System.Globalization;
using System.Runtime.Versioning;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition.Orchestration;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using CoreX.Hub.Infrastructure.Configuration;

using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.Cli.Commands;

/// <summary>
/// Running one attempt, and stopping one.
///
/// This is where a person meets the pipeline, and it is deliberately thin: it resolves what
/// the catalog already says, assembles the production orchestrator and reports what came
/// back. It decides nothing about outcomes. In particular it cannot accept anything - a
/// passing run stops at AwaitingAcceptance and prints the two commands a person would type
/// next, because acceptance is a human decision recorded through its own service.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RunCommands
{
    public static async Task<int> RunAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        TextWriter diagnostics,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var taskKey = args.PositionalAt(1)
            ?? throw new UsageException("hub run needs a task key, for example: hub run HUB-001");

        // Anything abandoned after its result commit is closed out before a new attempt is
        // considered, because until it is, the task it belongs to is still Running and this
        // command would refuse for the wrong reason.
        await RecoverPendingAsync(session, output, ct);

        // Resolved and composed by the shared planner, so a run started here and a run
        // started over HTTP cannot disagree about which profile implements, which reviews, or
        // whether this task is runnable at all.
        PlannedExecution planned;

        try
        {
            planned = await ExecutionLaunchPlanner.PlanAsync(
                session, taskKey, args.GetOption("org"), args.GetOption("agent"), ct);
        }
        catch (AmbiguousLaunchException ex)
        {
            // The console can tell the operator which flag to add; the API cannot. That is the
            // only reason this distinction exists, and it is handled here rather than in the
            // planner.
            throw new UsageException(ex.Candidates.Count == 0
                ? ex.Message
                : ex.Message.Replace("name the one to use", "pass --agent <name>", StringComparison.Ordinal));
        }

        var task = planned.Task;
        var profile = planned.AgentProfile;
        var composition = planned.Composition;

        // M5: an earlier attempt that produced no commit still owns its worktree, and this
        // says so once. It is not a reason to refuse - the work can be attempted again - and
        // nothing here deletes it.
        await WarnAboutPreservedWorktreesAsync(session, task, output, ct);

        var organization = planned.Organization;

        // One execution at a time for this installation, across processes. The orchestrator
        // enforces the same rule inside this process; neither is a substitute for the other.
        using var slot = new MachineExecutionSlot(session.DataDirectory).TryClaim();

        if (slot is null)
        {
            throw new TaskOperationException(
                "another hub process is already running an execution for this installation; "
                + "wait for it to finish or stop it with 'hub cancel'.");
        }

        var report = await RunWithCancellationAsync(composition, task.Id, profile.Name, ct);

        var execution = await session.Store.FindExecutionAsync(report.ExecutionId, ct)
            ?? throw new TaskOperationException("The execution disappeared as it finished.");

        var finished = await ExecutionLaunchPlanner.ResolveTaskAsync(session, taskKey, args.GetOption("org"), ct);

        await WriteSummaryAsync(
            session, output, finished, execution, report, profile.Name, organization.Slug.Value, ct);

        return report.Outcome == ExecutionOutcome.Passed ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    /// <summary>
    /// Runs the real pipeline while listening for another process asking it to stop.
    ///
    /// The listener is registered against this execution's own id, and only after the
    /// execution exists - so a request always names a particular attempt and can never be
    /// delivered to a later one. Cancellation reaches the orchestrator as an ordinary token,
    /// which is what its existing rules already understand: the agent is stopped, evidence is
    /// still captured, and no result commit is created after the caller has said stop.
    /// </summary>
    private static async Task<ExecutionRunReport> RunWithCancellationAsync(
        ExecutionComposition composition,
        Guid taskId,
        string agentProfileName,
        CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var signal = HubCancellation.Signal();
        IDisposable? listening = null;

        try
        {
            return await composition.Orchestrator.RunAsync(
                taskId,
                agentProfileName,
                stopping.Token,
                executionId => listening = signal?.Listen(executionId, stopping.Cancel));
        }
        finally
        {
            listening?.Dispose();
        }
    }

    public static async Task<int> CancelAsync(
        HubSession session,
        ArgParser args,
        TextWriter output,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var taskKey = args.PositionalAt(1)
            ?? throw new UsageException("hub cancel needs a task key, for example: hub cancel HUB-001");

        await RecoverPendingAsync(session, output, ct);

        // The same construction the HTTP surface gets. Every rule below it - what may be
        // cancelled, whether a running execution can be stopped, what is written and when -
        // has been the application's since Phase 5 and is untouched here.
        var result = await HubCancellation.Build(session.OpenStore)
            .CancelAsync(taskKey, args.GetOption("org"), args.GetOption("reason"), ct);

        Output.WriteField(output, "Task", result.TaskKey);
        Output.WriteField(output, "Result", result.State.ToString());
        Output.WriteField(output, "Task State", result.TaskState.ToString());

        if (result.ExecutionId is { } executionId)
        {
            Output.WriteField(output, "Execution", executionId.ToString("D"));
            Output.WriteField(output, "Execution State", result.ExecutionState?.ToString() ?? "(unknown)");
            Output.WriteField(output, "Result commit", result.ResultCommitSha ?? "(none)");
            Output.WriteField(output, "Worktree", result.WorktreePreserved ? "preserved" : "(no commitless work)");
        }

        Output.WriteField(output, "Detail", result.Detail);

        return result.State == TaskCancellationState.Cancelled ? ExitCodes.Success : ExitCodes.OperationFailed;
    }

    /// <summary>
    /// Closes out executions whose host died after the Hub had already committed their result.
    ///
    /// The session reports them when it opens; it cannot close them, because doing that
    /// honestly means either reading durable verification truth or measuring the commit again,
    /// and measuring needs the repository. This is the first place that has one.
    ///
    /// Nothing here can produce a second result commit: the recovery service is built without
    /// a workspace port at all, so the only thing it can reach is the verifier.
    /// </summary>
    private static async Task RecoverPendingAsync(HubSession session, TextWriter output, CancellationToken ct)
    {
        if (session.PendingRecovery.Count == 0)
        {
            return;
        }

        var config = new YamlHubConfigReader().Read(session.ConfigPath);

        foreach (var pending in session.PendingRecovery)
        {
            var task = await session.Store.FindTaskAsync(pending.TaskId, ct);

            if (task is null)
            {
                output.WriteLine($"recover: {pending.ExecutionId:D} has no task any more; left as it is.");
                continue;
            }

            var organization = await session.Store.FindOrganizationAsync(task.OrganizationId, ct);
            var repository = await session.Store.FindRepositoryAsync(task.RepositoryId, ct);

            if (organization is null || repository is null)
            {
                output.WriteLine(
                    $"recover: {pending.ExecutionId:D} names a repository that is no longer in the catalogue; "
                    + "left as it is.");
                continue;
            }

            var verificationProfile = await session.Store.FindDefaultVerificationProfileAsync(repository.Id, ct);

            try
            {
                // Recovery measures a commit that already exists; it never runs an agent, so
                // there is no profile to resolve a runtime for. The reviewer is still resolved,
                // because whether this repository is reviewed decides whether an unreviewed
                // result may be called Passed - and recovery cannot review one.
                var reviewer = await ExecutionLaunchPlanner.ResolveReviewerAsync(
                    session, config, organization, repository, ct);

                var composition = ExecutionComposition.Create(
                    session, config, organization, repository, verificationProfile, null, reviewer,
                    TimeProvider.System);

                var service = new PostCommitRecoveryService(
                    session.OpenStore, composition.Verifier, TimeProvider.System, composition.ReviewRequired);

                var report = await service.RecoverAsync([pending], ct);

                output.WriteLine("recover: " + report.Describe());
            }
            catch (HubConfigurationException ex)
            {
                // Recovery needs the local toolchain and the local runtimes. Not having them
                // is a reason this execution stays as it is, not a reason the command the
                // operator actually typed cannot run.
                output.WriteLine($"recover: {pending.ExecutionId:D} could not be recovered here: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// M5, in one line and nothing more.
    ///
    /// A previous attempt that never reached a result commit keeps its worktree, because it
    /// holds the only copy of whatever was done. That matters to whoever is about to start
    /// another attempt, so it is said out loud - once per attempt, without a subsystem to
    /// track it, and without touching anything on disk.
    /// </summary>
    private static async Task WarnAboutPreservedWorktreesAsync(
        HubSession session,
        HubTask task,
        TextWriter output,
        CancellationToken ct)
    {
        var executions = await session.Store.ListExecutionsForTaskAsync(task.Id, ct);

        foreach (var previous in executions
                     .Where(e => e.IsTerminal
                                 && e.ResultCommitSha is null
                                 && e.WorkspaceState != WorkspaceState.Released)
                     .OrderBy(e => e.AttemptNo))
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"warning: attempt {previous.AttemptNo} ({previous.State}) produced no result commit, so its "
                + $"worktree is preserved at {previous.WorktreePath}; nothing here removes it."));
        }
    }

    /// <summary>
    /// The evidence summary: what the machine recorded, read back from what was persisted.
    ///
    /// Every line below comes from the execution row, its steps, its verification commands
    /// or the task - never from anything the agent said about itself. An agent that reported
    /// success cannot make this print a passing run.
    /// </summary>
    private static async Task WriteSummaryAsync(
        HubSession session,
        TextWriter output,
        HubTask task,
        DomainExecution execution,
        ExecutionRunReport report,
        string agentProfileName,
        string organizationSlug,
        CancellationToken ct)
    {
        var steps = await session.Store.ListExecutionStepsAsync(execution.Id, ct);
        var commands = await session.Store.ListVerificationCommandsAsync(execution.Id, ct);

        output.WriteLine();
        Output.WriteField(output, "Task", $"{task.TaskKey} ({task.State})");
        Output.WriteField(output, "Attempt", execution.AttemptNo.ToString(CultureInfo.InvariantCulture));
        Output.WriteField(output, "Execution", execution.Id.ToString("D"));
        Output.WriteField(output, "Agent", agentProfileName);
        Output.WriteField(output, "Base commit", execution.BaseCommitSha);
        Output.WriteField(output, "Result commit", execution.ResultCommitSha ?? "(none)");
        Output.WriteField(output, "Context pack", execution.ContextPackHash ?? "(none)");
        Output.WriteField(output, "Branch", execution.BranchName);
        Output.WriteField(output, "Workspace", execution.WorktreePath);
        Output.WriteField(output, "Outcome", report.Describe());

        if (report.SnapshotResidual is { } residual)
        {
            Output.WriteField(output, "Snapshot", residual);
        }

        output.WriteLine();
        output.WriteLine("steps:");

        Output.WriteTable(
            output,
            ["STEP", "STATE", "REASON"],
            steps.OrderBy(s => s.Order).Select(s => new[]
            {
                s.Kind.ToString(),
                s.State.ToString(),
                s.StateReason ?? string.Empty,
            }));

        output.WriteLine();
        output.WriteLine("verification:");

        Output.WriteTable(
            output,
            ["COMMAND", "REQUIRED", "STATUS", "EXIT", "TESTS"],
            commands.OrderBy(c => c.Order).Select(c => new[]
            {
                c.StepName,
                c.Required ? "yes" : "no",
                c.Status.ToString(),
                c.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                c.TestsPassed is null && c.TestsFailed is null
                    ? string.Empty
                    : $"{c.TestsPassed ?? 0} passed / {c.TestsFailed ?? 0} failed",
            }));

        await WriteCriteriaAsync(session, output, task, execution, organizationSlug, ct);

        if (report.Outcome == ExecutionOutcome.Passed)
        {
            output.WriteLine();
            output.WriteLine("Next:");
            output.WriteLine($"  hub evidence show {task.TaskKey}#{execution.AttemptNo} --diff");
            output.WriteLine($"  hub accept {task.TaskKey}#{execution.AttemptNo}");
        }
    }

    /// <summary>
    /// What acceptance will ask, and what it can already answer.
    ///
    /// Read through the acceptance service's own preview, so what is shown here is exactly
    /// what a person will see when they decide in another process later - reconstructed from
    /// the recorded verification commands, not from anything this run still holds in memory.
    /// Human criteria are listed as outstanding, because nothing here may answer them.
    /// </summary>
    private static async Task WriteCriteriaAsync(
        HubSession session,
        TextWriter output,
        HubTask task,
        DomainExecution execution,
        string organizationSlug,
        CancellationToken ct)
    {
        if (task.AcceptanceCriteria.Count == 0)
        {
            return;
        }

        var preview = await session.Acceptance.PreviewAsync(
            task.TaskKey.Value, execution.AttemptNo, organizationSlug, ct);

        output.WriteLine();
        output.WriteLine("machine criteria:");

        Output.WriteTable(
            output,
            ["#", "REF", "VERDICT", "STATUS", "TEXT"],
            preview.MachineCriteria.OrderBy(c => c.Order).Select(c => new[]
            {
                c.Order.ToString(CultureInfo.InvariantCulture),
                c.VerificationRef,
                c.Verdict.ToString(),
                c.VerificationStatus.ToString(),
                c.Text,
            }));

        foreach (var problem in preview.MachineTruthProblems)
        {
            output.WriteLine("machine truth problem: " + problem);
        }

        output.WriteLine();
        output.WriteLine("human criteria (pending):");

        Output.WriteTable(
            output,
            ["#", "TEXT"],
            preview.HumanCriteria.OrderBy(c => c.Order).Select(c => new[]
            {
                c.Order.ToString(CultureInfo.InvariantCulture),
                c.Text,
            }));
    }
}
