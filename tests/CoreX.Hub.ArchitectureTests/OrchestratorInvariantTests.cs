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

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
