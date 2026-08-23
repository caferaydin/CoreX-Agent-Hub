using System.Text.RegularExpressions;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// The invariants Phase 5 closes with: what the two new commands may and may not do, and what
/// recovery may and may not reach.
///
/// These are ownership questions rather than behaviour questions - behaviour is proved by the
/// Loop Zero suite, which runs the real commands against a real repository. What is guarded
/// here is the shape that makes those behaviours the only possible ones.
/// </summary>
public sealed class LoopZeroInvariantTests
{
    private static string Source(string fileName) =>
        File.ReadAllText(
            RepoLayout.ProductionSourceFiles.Single(f =>
                string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal)));

    /// <summary>Strips comments, so a guard never passes or fails on prose.</summary>
    private static string CodeOnly(string source) =>
        string.Join(
            '\n',
            source.Split('\n')
                .Select(l => l.TrimStart())
                .Where(l => !l.StartsWith("//", StringComparison.Ordinal)
                            && !l.StartsWith("/*", StringComparison.Ordinal)
                            && !l.StartsWith("*", StringComparison.Ordinal)));

    [Fact]
    public void Hub_run_drives_the_production_orchestrator_and_nothing_of_its_own()
    {
        var run = CodeOnly(Source("RunCommands.cs"));

        // The one way a run happens.
        Assert.Contains("composition.Orchestrator.RunAsync(", run, StringComparison.Ordinal);

        // And there is no second pipeline hiding here: no workspace, agent, context, gate or
        // verification call of its own.
        foreach (var forbidden in new[]
                 {
                     "PrepareAsync(", "CreateResultCommitAsync(", "CaptureAsync(",
                     "VerifyAsync(", "IAgentRuntime", "ExecutionSafetyGates", "SkipGates",
                 })
        {
            Assert.False(run.Contains(forbidden, StringComparison.Ordinal), $"RunCommands.cs: {forbidden}");
        }
    }

    [Fact]
    public void Hub_run_can_never_accept_anything()
    {
        var run = CodeOnly(Source("RunCommands.cs"));

        // A run may show what acceptance would ask; it may not answer it.
        Assert.Contains("PreviewAsync(", run, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "AcceptAsync(", "AcceptanceRequest", "HumanVerdict", "UseOverride" })
        {
            Assert.False(run.Contains(forbidden, StringComparison.Ordinal), $"RunCommands.cs: {forbidden}");
        }

        // Nor may the orchestrator: Accepted is a person's decision, recorded elsewhere.
        foreach (var file in RepoLayout.ProductionSourceFiles.Where(f =>
                     f.Contains("Orchestration", StringComparison.Ordinal)))
        {
            var code = CodeOnly(File.ReadAllText(file));

            Assert.False(code.Contains(".Accept(", StringComparison.Ordinal), RepoLayout.Relative(file));
            Assert.False(code.Contains("AcceptByReviewOverride", StringComparison.Ordinal), RepoLayout.Relative(file));
            Assert.False(code.Contains("AddAcceptanceDecision", StringComparison.Ordinal), RepoLayout.Relative(file));
        }
    }

    [Fact]
    public void Cancelling_running_work_cannot_be_a_state_change_on_its_own()
    {
        var service = CodeOnly(Source("TaskCancellationService.cs"));

        // The order is the invariant: ask the owner to stop, watch the database until it says
        // it stopped, and only then record the decision.
        var request = service.IndexOf("_signal.TryRequest(", StringComparison.Ordinal);
        var observe = service.IndexOf("observed.IsTerminal", StringComparison.Ordinal);
        var record = service.IndexOf("task.CancelByOperator(", StringComparison.Ordinal);

        Assert.True(request > 0 && observe > request, "cancellation must observe after it signals");
        Assert.True(record > observe, "the decision must be recorded after the stop is observed");

        // Two paths reach the decision, and the running one is not allowed to skip the wait:
        // the only call is inside the method that writes it.
        Assert.Single(Regex.Matches(service, Regex.Escape("task.CancelByOperator(")));

        // And nothing about the execution is rewritten to match the task.
        foreach (var forbidden in new[] { ".Terminate(", ".Complete(", "RecordResultCommit", "execution.Advance(" })
        {
            Assert.False(service.Contains(forbidden, StringComparison.Ordinal), $"TaskCancellationService: {forbidden}");
        }
    }

    [Fact]
    public void Only_an_operator_can_produce_a_cancelled_task()
    {
        // The domain edge exists once, and orchestration cannot take it.
        foreach (var file in RepoLayout.ProductionSourceFiles.Where(f =>
                     f.Contains("Orchestration", StringComparison.Ordinal)))
        {
            Assert.False(
                CodeOnly(File.ReadAllText(file)).Contains("CancelByOperator", StringComparison.Ordinal),
                RepoLayout.Relative(file));
        }

        var callers = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("CancelByOperator(", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Equal(2, callers.Count);
        Assert.Contains(callers, c => c.EndsWith("HubTask.cs", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.EndsWith("TaskCancellationService.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Recovery_after_a_result_commit_cannot_reach_a_workspace_an_agent_or_history()
    {
        var recovery = CodeOnly(Source("PostCommitRecoveryService.cs"));

        // Not a matter of care: there is no port here that could make a second commit, run an
        // agent, or touch a ref.
        foreach (var forbidden in new[]
                 {
                     "IExecutionWorkspace", "CreateResultCommitAsync", "IAgentRuntimeGateway",
                     "IExecutionContextBuilder", "reset", "checkout", "update-ref", "amend", "rebase",
                     "RecordResultCommit",
                 })
        {
            Assert.False(recovery.Contains(forbidden, StringComparison.Ordinal), $"PostCommitRecoveryService: {forbidden}");
        }

        // What it can reach is exactly one thing: the verifier.
        var constructor = Assert.Single(typeof(PostCommitRecoveryService).GetConstructors());
        var parameters = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IExecutionVerifier), parameters);
        Assert.DoesNotContain(parameters, t => t.Name.Contains("Workspace", StringComparison.Ordinal));
        Assert.DoesNotContain(parameters, t => t.Name.Contains("Agent", StringComparison.Ordinal));
    }

    [Fact]
    public void The_result_commit_has_exactly_one_author_in_the_whole_system()
    {
        var callers = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("CreateResultCommitAsync(", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        // The workspace module implements it, the port declares it, the composition adapter
        // forwards it, and the orchestrator calls it once. Nothing else.
        Assert.Equal(4, callers.Count);
        Assert.Contains(callers, c => c.EndsWith("WorkspaceManager.cs", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.EndsWith("OrchestrationPorts.cs", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.EndsWith("WorkspaceGateway.cs", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.EndsWith("ExecutionOrchestrator.Gates.cs", StringComparison.Ordinal));

        var gates = CodeOnly(Source("ExecutionOrchestrator.Gates.cs"));
        Assert.Single(Regex.Matches(gates, Regex.Escape("CreateResultCommitAsync(")));
    }

    [Fact]
    public void The_preserved_worktree_warning_only_warns()
    {
        var run = CodeOnly(Source("RunCommands.cs"));

        var start = run.IndexOf("WarnAboutPreservedWorktreesAsync", StringComparison.Ordinal);
        Assert.True(start > 0);

        foreach (var forbidden in new[]
                 {
                     "Directory.Delete", "File.Delete", "ReleaseAsync", "--force", "worktree\", \"remove",
                 })
        {
            Assert.False(run.Contains(forbidden, StringComparison.Ordinal), $"RunCommands.cs: {forbidden}");
        }
    }

    [Fact]
    public void There_is_no_command_that_commits_a_result_on_a_persons_behalf()
    {
        var cli = Source("HubCli.cs");

        foreach (var forbidden in new[] { "commit-result", "force-accept", "auto-accept" })
        {
            Assert.False(cli.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"HubCli.cs: {forbidden}");
        }

        // The commands that exist are the ones Phase 5 declares.
        foreach (var expected in new[] { "\"run\"", "\"cancel\"", "\"accept\"", "\"request-changes\"", "\"reject\"" })
        {
            Assert.Contains(expected, cli, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_in_the_shipped_system_can_reach_a_network_or_a_provider()
    {
        var pattern = new Regex(
            @"HttpClient|WebClient|HttpRequestMessage|System\.Net\.Http|WebSocket|TcpClient|Socket\(",
            RegexOptions.CultureInvariant);

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var code = CodeOnly(File.ReadAllText(file));

            Assert.False(pattern.IsMatch(code), RepoLayout.Relative(file) + " reaches the network");
        }

        // And no provider is named anywhere in production code: the runtime a profile gets is
        // whatever composition registered under its key.
        //
        // The secret pattern catalog is the one exception, and it proves the rule: it names
        // vendors because it recognises their credentials, which is the opposite of depending
        // on them.
        var scanned = RepoLayout.ProductionSourceFiles
            .Where(f => !Path.GetFileName(f).Equals("SecretPatternCatalog.cs", StringComparison.Ordinal))
            .ToList();

        foreach (var provider in new[] { "ClaudeCode", "Codex", "Gemini", "Qwen", "OpenAI", "Anthropic" })
        {
            foreach (var file in scanned)
            {
                Assert.False(
                    CodeOnly(File.ReadAllText(file)).Contains(provider, StringComparison.OrdinalIgnoreCase),
                    $"{RepoLayout.Relative(file)} names the provider {provider}");
            }
        }
    }

    [Fact]
    public void A_baseline_comparison_needs_measurements_from_both_sides()
    {
        var gateway = CodeOnly(Source("VerificationGateway.cs"));

        // The fabrication this replaced: comparing the baseline against itself when the current
        // run produced no counters, which reads as "unchanged" about a run nobody measured.
        Assert.DoesNotContain("?? entry.Summary", gateway, StringComparison.Ordinal);
        Assert.Contains("nothing to compare", gateway, StringComparison.Ordinal);

        var compare = gateway.IndexOf("BaselineComparer.Compare(", StringComparison.Ordinal);
        var guard = gateway.IndexOf("current.TestSummary is { } summary", StringComparison.Ordinal);

        Assert.True(guard > 0 && guard < compare, "the comparison must be guarded by both sides existing");
    }

    [Fact]
    public void One_execution_at_a_time_is_claimed_where_other_processes_can_see_it()
    {
        var slot = CodeOnly(Source("MachineExecutionSlot.cs"));

        // A file handle rather than a mutex or a semaphore, for two stated reasons: no thread
        // affinity, and the operating system releases it when the holder is killed.
        Assert.Contains("FileShare.None", slot, StringComparison.Ordinal);
        Assert.DoesNotContain("new Mutex(", slot, StringComparison.Ordinal);
        Assert.DoesNotContain("new Semaphore(", slot, StringComparison.Ordinal);

        // The in-process rule is untouched: still one, still local, still not a distributed lock.
        Assert.Equal(1, new ExecutionOrchestratorOptions().MaxConcurrentExecutions);

        var run = CodeOnly(Source("RunCommands.cs"));
        Assert.Contains("MachineExecutionSlot(", run, StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_layer_asks_for_cancellation_without_knowing_what_a_handle_is()
    {
        var ports = Source("ExecutionControlPorts.cs");

        // The port is a port: no operating system type crosses it.
        foreach (var forbidden in new[] { "EventWaitHandle", "Mutex", "FileStream", "Console", "Process" })
        {
            Assert.False(ports.Contains(forbidden, StringComparison.Ordinal), $"ExecutionControlPorts.cs: {forbidden}");
        }

        // And the implementation is in composition, keyed by the execution id.
        var named = CodeOnly(Source("NamedExecutionCancellation.cs"));

        Assert.Contains("EventWaitHandle", named, StringComparison.Ordinal);
        Assert.Contains("executionId.ToString(\"N\")", named, StringComparison.Ordinal);
        Assert.DoesNotContain("taskId", named, StringComparison.OrdinalIgnoreCase);

        var implementations = typeof(TaskCancellationService).Assembly
            .GetTypes()
            .Where(t => typeof(IExecutionCancellationSignal).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();

        Assert.Empty(implementations);
    }

    [Fact]
    public void Completion_means_the_same_thing_however_an_execution_reaches_it()
    {
        // Two callers finish an execution - the pipeline and recovery - and a passing run has
        // to hand the task to a person from either. The rule is written once.
        var callers = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("ExecutionCompletion.Apply(", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Equal(2, callers.Count);
        Assert.Contains(callers, c => c.EndsWith("ExecutionOrchestrator.Finish.cs", StringComparison.Ordinal));
        Assert.Contains(callers, c => c.EndsWith("PostCommitRecoveryService.cs", StringComparison.Ordinal));

        // MarkAwaitingAcceptance has exactly one caller in the whole system, and it is that rule.
        var awaiting = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("MarkAwaitingAcceptance(", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Equal(2, awaiting.Count);
        Assert.Contains(awaiting, c => c.EndsWith("HubTask.cs", StringComparison.Ordinal));
        Assert.Contains(awaiting, c => c.EndsWith("ExecutionCompletion.cs", StringComparison.Ordinal));
    }
}
