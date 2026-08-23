using System.Reflection;
using CoreX.Hub.Application.Orchestration;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Phase 5C guards. The orchestrator now exists, so the checks the earlier phases armed
/// have a real subject.
/// </summary>
public sealed class OrchestratorInvariantTests
{
    private static IEnumerable<string> OrchestrationSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar
                + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

    /// <summary>Everything in the Application layer, not only the orchestrator's own folder.</summary>
    private static IEnumerable<string> ApplicationSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

    /// <summary>
    /// The orchestrator plus the composition adapters that sit directly under it. Together
    /// these are the code that decides which token a stage runs on.
    /// </summary>
    private static IEnumerable<string> SequencingSourceFiles() =>
        OrchestrationSourceFiles().Concat(RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Cli" + Path.DirectorySeparatorChar
                + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)));

    [Fact]
    public void The_orchestrator_exists_so_these_guards_inspect_something()
    {
        var names = OrchestrationSourceFiles().Select(Path.GetFileName).ToList();

        Assert.Contains("ExecutionOrchestrator.cs", names);
        Assert.NotNull(typeof(ExecutionOrchestrator));
    }

    [Fact]
    public void The_orchestrator_sequences_and_implements_nothing()
    {
        // Every mechanism belongs to a lower module and arrives through a port.
        string[] forbidden =
        [
            "Process.Start", "ProcessStartInfo", "\"worktree\"", "rev-parse", "git ",
            "Regex", "sk-", "ghp_", "AKIA", "DbContext", "SqlConnection", "HttpClient",
            "EvidenceBlobStore", "VerificationEngine", "TrxReportParser", "ContextPackBuilder",
        ];

        foreach (var file in OrchestrationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in forbidden)
            {
                Assert.False(code.Contains(construct, StringComparison.Ordinal), $"{name}: {construct}");
            }
        }
    }

    [Fact]
    public void The_orchestrator_names_no_provider_and_branches_on_no_runtime_identity()
    {
        foreach (var file in OrchestrationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var word in new[]
                     {
                         "Claude", "Codex", "Gemini", "Qwen", "Anthropic", "OpenAI", "Ollama", "vLLM",
                     })
            {
                Assert.False(code.Contains(word, StringComparison.OrdinalIgnoreCase), $"{name}: {word}");
            }

            // Resolution is data driven: the profile names a runtime and composition answers.
            Assert.DoesNotContain("switch (profile.Provider", code, StringComparison.Ordinal);
            Assert.DoesNotContain("switch (profile.RuntimeKey", code, StringComparison.Ordinal);
            Assert.DoesNotContain("RuntimeKey ==", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Provider ==", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_orchestrator_knows_nothing_about_access_or_billing()
    {
        foreach (var file in OrchestrationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var concept in new[]
                     {
                         "ApiKey", "AccessMode", "CredentialRef", "Subscription", "Billing",
                         "Quota", "BaseUrl", "Endpoint", "ChatCompletion",
                     })
            {
                Assert.False(code.Contains(concept, StringComparison.OrdinalIgnoreCase), $"{name}: {concept}");
            }
        }
    }

    [Fact]
    public void The_orchestrator_never_accepts_a_task()
    {
        // Accepted is a human decision. Nothing here can reach it, and the aggregate has no
        // method that would let it.
        foreach (var file in OrchestrationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(code.Contains("TaskState.Accepted", StringComparison.Ordinal), $"{name}");
            Assert.False(code.Contains("TaskState.Rejected", StringComparison.Ordinal), $"{name}");
            Assert.False(code.Contains("AcceptanceDecision", StringComparison.Ordinal), $"{name}");
        }

        var taskMethods = typeof(Domain.Tasks.HubTask)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        // AddAcceptanceCriterion is about declaring criteria, not about accepting: what must
        // not exist is a transition into a human's decision.
        Assert.DoesNotContain(taskMethods, m => m.StartsWith("Accept", StringComparison.Ordinal));
        Assert.DoesNotContain(taskMethods, m => m.StartsWith("Reject", StringComparison.Ordinal));
        Assert.DoesNotContain(taskMethods, m => m.Contains("MarkAccepted", StringComparison.Ordinal));
    }

    [Fact]
    public void There_is_one_result_commit_authority_and_it_is_the_workspace()
    {
        // The orchestrator asks a port. The port's only implementation calls the workspace
        // module, which is the sole place a Hub commit is created.
        foreach (var file in OrchestrationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            Assert.DoesNotContain("\"commit\"", code, StringComparison.Ordinal);
        }

        var manager = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), "WorkspaceManager.cs", StringComparison.Ordinal));

        Assert.Contains("\"commit\"", File.ReadAllText(manager), StringComparison.Ordinal);
    }

    [Fact]
    public void No_push_merge_or_deploy_path_was_introduced()
    {
        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[] { "\"push\"", "\"merge\"", "\"pull\"", "\"fetch\"", "deploy" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void No_production_code_carries_a_stand_in_for_a_base_commit()
    {
        // The null object id is the shape a placeholder takes when a row has to exist before
        // a commit has been resolved: right length, hexadecimal, and naming nothing. Every
        // spelling of "we will fill this in later" is refused, because the value ends up in
        // a baseline key and a context pack long before anyone notices it was never real.
        string[] forbidden =
        [
            "0000000000000000000000000000000000000000",
            "PlaceholderBaseCommit",
            "PendingBaseCommit",
            "BaseCommitPending",
            "UnknownBaseCommit",
        ];

        foreach (var file in RepoLayout.ProductionSourceFiles)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in forbidden)
            {
                Assert.False(code.Contains(construct, StringComparison.OrdinalIgnoreCase), $"{name}: {construct}");
            }
        }
    }

    [Fact]
    public void Only_the_execution_constructor_writes_the_base_commit()
    {
        // Provenance arrives once, through the constructor. Anything else that assigned it
        // would be a second authority over what an execution was based on.
        var executionSources = RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Domain" + Path.DirectorySeparatorChar
                + "Executions" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

        var writers = executionSources
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("BaseCommitSha =", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Execution.cs"], writers);
    }

    [Fact]
    public void The_unmaterialised_worktree_path_never_leaves_the_row_it_was_written_to()
    {
        // An execution exists before its worktree does, so its path is a placeholder for a
        // while. That is tolerable only as long as nothing treats it as a location: it is
        // written once, at construction, and no other production file mentions it - so no
        // filesystem or git call can be reached with it.
        var mentions = RepoLayout.ProductionSourceFiles
            .Where(f => File.ReadAllText(f).Contains("(not created yet)", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ExecutionOrchestrator.Persistence.cs"], mentions);

        // And it is spelled as a sentence, not as a path: nothing can accidentally combine,
        // root or resolve it into something that looks real.
        Assert.Contains(
            "(not created yet)",
            File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
                string.Equals(
                    Path.GetFileName(f), "ExecutionOrchestrator.Persistence.cs", StringComparison.Ordinal))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_immunity_is_bounded_rather_than_absolute()
    {
        // CancellationToken.None is immunity with no end to it. The mandatory sections do
        // need a token the caller cannot cancel - evidence is not optional work - but they
        // need one that expires, so a stuck command cannot hold a cancelled run open for
        // ever by being the very thing that was supposed to close it.
        foreach (var file in SequencingSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(code.Contains("CancellationToken.None", StringComparison.Ordinal), $"{name}");
        }

        // And the bounded alternative exists, with a budget behind it.
        Assert.Contains(
            nameof(MandatoryWorkScope),
            File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
                string.Equals(
                    Path.GetFileName(f), "ExecutionOrchestrator.Pipeline.cs", StringComparison.Ordinal))),
            StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => new MandatoryWorkScope(TimeSpan.Zero));
    }

    [Fact]
    public void The_reconciler_looks_for_a_result_commit_before_it_writes_a_terminal_state()
    {
        var source = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "StaleExecutionReconciler.cs", StringComparison.Ordinal)));

        var code = CodeOnly(source);

        var checkedForCommit = code.IndexOf("execution.ResultCommitSha is", StringComparison.Ordinal);
        var terminate = code.IndexOf("execution.Terminate(", StringComparison.Ordinal);

        Assert.True(checkedForCommit >= 0, "the reconciler must ask whether a result commit exists");
        Assert.True(terminate > checkedForCommit, "it must ask before it writes a terminal state");

        // The stranded case is a reconciliation result, never a state or an outcome, so no
        // new vocabulary was invented to make it persistable.
        Assert.DoesNotContain("ExecutionState.RecoveryRequired", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionOutcome.", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskState.Accepted", code, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "RecoveryRequired",
            string.Join("\n", Enum.GetNames<Domain.Executions.ExecutionState>()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_layer_builds_no_git_command_anywhere()
    {
        // Not just the orchestrator's folder: nothing in the Application layer may reach for
        // git, because the moment it does, "the workspace module owns git" stops being true.
        string[] forbidden =
        [
            "Process.Start", "ProcessStartInfo", "\"worktree\"", "rev-parse", "\"commit\"",
            "GitCommand", "IGitCommandExecutor", "\"rev-list\"", "\"status\"", "\"diff\"",
        ];

        foreach (var file in ApplicationSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in forbidden)
            {
                Assert.False(code.Contains(construct, StringComparison.Ordinal), $"{name}: {construct}");
            }
        }
    }

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
