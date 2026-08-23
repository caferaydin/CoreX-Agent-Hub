using System.Reflection;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

// The Phase 2 assembly introduces a CoreX.Hub.Execution namespace, so the domain type of
// the same short name is aliased here to keep the reference unambiguous.
using DomainExecution = CoreX.Hub.Domain.Executions.Execution;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Guards for the one thing in the system a machine never does on its own.
///
/// Accepted and Rejected are a person's words. So the questions here are about authority
/// and about restraint: exactly one service may say them, it may not say anything else
/// while doing so, and nothing it touches on the way may be a value a person supplied in
/// place of evidence.
/// </summary>
public sealed class AcceptanceInvariantTests
{
    private static IEnumerable<string> AcceptanceSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar
                + "Acceptance" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

    [Fact]
    public void Only_the_acceptance_service_can_accept_or_reject_a_task()
    {
        // The aggregate now offers the edges; what must stay true is that one service takes
        // them. Phase 5C's guard said "nothing may": this is that guard grown up, not
        // relaxed.
        var callers = RepoLayout.ProductionSourceFiles
            .Where(f =>
            {
                var code = CodeOnly(File.ReadAllText(f));

                return code.Contains(".Accept(", StringComparison.Ordinal)
                       || code.Contains(".AcceptByReviewOverride(", StringComparison.Ordinal)
                       || code.Contains(".Reject(", StringComparison.Ordinal);
            })
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AcceptanceService.cs"], callers);

        // The transitions themselves exist exactly once, on the task.
        var taskMethods = typeof(HubTask)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("Accept", taskMethods);
        Assert.Contains("AcceptByReviewOverride", taskMethods);
        Assert.Contains("Reject", taskMethods);
    }

    [Fact]
    public void The_orchestrator_still_cannot_reach_a_human_decision()
    {
        var orchestration = RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar
                + "Orchestration" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

        foreach (var file in orchestration)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[]
                     {
                         "TaskState.Accepted", "TaskState.Rejected", "AcceptanceDecision",
                         "AcceptanceService", ".Accept(", ".Reject(",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void Acceptance_never_writes_anything_about_the_execution()
    {
        // I-A7. A decision is about work that already happened; rewriting the record of it
        // would make the decision unfalsifiable.
        foreach (var file in AcceptanceSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var mutation in new[]
                     {
                         "execution.Complete(", "execution.Terminate(", "execution.Advance(",
                         "execution.RecordResultCommit(", "execution.RecordWorkspace(",
                         "AddCommandResult(", "AddFinding(", "AddExecution(", "AddExecutionStep(",
                     })
            {
                Assert.False(code.Contains(mutation, StringComparison.Ordinal), $"{name}: {mutation}");
            }
        }

        // And it has no way to: the outcome has no public setter to reach.
        Assert.Null(typeof(DomainExecution).GetProperty(nameof(DomainExecution.Outcome))!.GetSetMethod(nonPublic: false));
    }

    [Fact]
    public void A_machine_verdict_can_never_be_supplied_from_outside()
    {
        // I-A6. Nothing a caller passes in may name a machine result, so there is no value
        // to overrule the evidence with.
        var inputs = new[] { typeof(AcceptanceRequest), typeof(TaskDecisionRequest) };

        foreach (var input in inputs)
        {
            foreach (var property in input.GetProperties())
            {
                Assert.NotEqual(typeof(MachineCriterionVerdict), property.PropertyType);
                Assert.NotEqual(typeof(VerificationCommandStatus), property.PropertyType);

                Assert.False(
                    property.Name.Contains("Machine", StringComparison.OrdinalIgnoreCase),
                    $"{input.Name}.{property.Name}");
            }
        }

        // The machine result type is output only: it is on the result and the preview, and
        // on neither request.
        Assert.Contains(
            typeof(AcceptanceResult).GetProperties(),
            p => p.PropertyType == typeof(IReadOnlyList<MachineCriterionResult>));
    }

    [Fact]
    public void Machine_truth_is_never_inferred_from_an_outcome_or_an_exit_code()
    {
        foreach (var file in AcceptanceSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            // An execution that passed says the required steps passed; it says nothing about
            // any particular criterion, and reading it that way would answer the wrong
            // question.
            foreach (var shortcut in new[]
                     {
                         "ExitCode == 0", "ExitCode is 0",
                         "Outcome == ExecutionOutcome.Passed ? MachineCriterionVerdict",
                     })
            {
                Assert.False(code.Contains(shortcut, StringComparison.Ordinal), $"{name}: {shortcut}");
            }
        }

        // The one rule that decides a machine criterion is the recorded status.
        var criteria = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "AcceptanceService.Criteria.cs", StringComparison.Ordinal)));

        Assert.Contains("command.IsPass ? MachineCriterionVerdict.Met", criteria, StringComparison.Ordinal);
    }

    [Fact]
    public void Truth_that_could_not_be_established_never_becomes_a_verdict()
    {
        var criteria = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "AcceptanceService.Criteria.cs", StringComparison.Ordinal)));

        // Unavailable and an unresolved reference are collected as problems, not mapped to
        // a verdict, and a problem stops the decision.
        Assert.Contains("VerificationCommandStatus.Unavailable", criteria, StringComparison.Ordinal);
        Assert.Contains("problems.Add(", criteria, StringComparison.Ordinal);
        Assert.Contains("EnsureMachineTruthEstablished", criteria, StringComparison.Ordinal);

        // There are only two verdicts, so there is no third value for "we could not tell"
        // to quietly become.
        Assert.Equal(["Met", "NotMet"], Enum.GetNames<MachineCriterionVerdict>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_override_set_is_closed_and_denies_by_default()
    {
        var rules = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "AcceptanceService.Rules.cs", StringComparison.Ordinal)));

        // The decision is delegated to the frozen rule rather than restated, and that rule
        // is exhaustively tested elsewhere with an explicit deny default.
        Assert.Contains("AcceptanceRules.IsReviewOverrideEligible", rules, StringComparison.Ordinal);
        Assert.Contains("AcceptanceRules.IsPlainAcceptanceEligible", rules, StringComparison.Ordinal);

        foreach (var outcome in Enum.GetValues<ExecutionOutcome>())
        {
            var expected = outcome == ExecutionOutcome.ReviewBlocked;
            Assert.Equal(expected, AcceptanceRules.IsReviewOverrideEligible(outcome));
        }

        // Written as an inverted permissive test, it would let a new outcome through.
        Assert.DoesNotContain("!= ExecutionOutcome.", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void Acceptance_reaches_no_git_no_process_and_no_provider()
    {
        // A decision is a record. Nothing about it merges, pushes, deploys or runs anything.
        string[] forbidden =
        [
            "Process.Start", "ProcessStartInfo", "\"worktree\"", "rev-parse", "\"commit\"",
            "\"push\"", "\"merge\"", "deploy", "publish", "HttpClient", "DbContext",
            "Claude", "Codex", "Gemini", "Qwen", "Anthropic", "OpenAI", "Ollama",
            "SecretPatternCatalog", "ReviewEngine", "commit-result",
        ];

        foreach (var file in AcceptanceSourceFiles())
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
    public void The_command_line_offers_no_reviewer_and_no_way_to_commit_on_a_persons_behalf()
    {
        var usage = Cli.HubCli.UsageText;

        Assert.Contains("hub accept", usage, StringComparison.Ordinal);
        Assert.Contains("hub reject", usage, StringComparison.Ordinal);
        Assert.Contains("hub evidence show", usage, StringComparison.Ordinal);

        // Phase 5E adds running and stopping work. Review is still nobody's job here, and
        // there is still no command that would make a commit or a decision for a person.
        foreach (var later in new[] { "hub review", "commit-result", "hub merge", "hub push" })
        {
            Assert.DoesNotContain(later + " ", usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Review_blocking_is_decided_in_exactly_one_place()
    {
        // A per-finding boolean cannot express the rule: a Critical blocks alone, a Major
        // blocks by count. So the judgement lives on the collection, and nothing else may
        // keep a second copy of it.
        var deciders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f))
                .Contains("FindingSeverity.Critical", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["ReviewFindingGate.cs"], deciders);

        // Acceptance asks the gate rather than restating it, and keeps no threshold of
        // its own.
        foreach (var file in AcceptanceSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            Assert.False(code.Contains("FindingSeverity", StringComparison.Ordinal), $"{name}");
            Assert.False(code.Contains("MajorThreshold", StringComparison.Ordinal), $"{name}");
            Assert.False(code.Contains("OutOfScope", StringComparison.Ordinal), $"{name}");
        }

        Assert.Contains(
            "ReviewFindingGate.HasBlockingFindings",
            File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
                string.Equals(Path.GetFileName(f), "AcceptanceService.Rules.cs", StringComparison.Ordinal))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Minor_and_info_can_never_block_and_out_of_scope_never_counts()
    {
        // Asserted against the gate itself rather than its source text, so the rule is
        // proven rather than pattern matched.
        Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Minor)]));
        Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Info)]));

        Assert.True(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Critical)]));
        Assert.True(ReviewFindingGate.HasBlockingFindings([Finding(FindingSeverity.Major)]));

        foreach (var severity in Enum.GetValues<FindingSeverity>())
        {
            Assert.False(ReviewFindingGate.HasBlockingFindings([Finding(severity, outOfScope: true)]));
        }

        // The frozen default. Moving it is G7-1, and it moves here.
        Assert.Equal(1, ReviewFindingGate.MajorThreshold);
    }

    [Fact]
    public void Cleanup_happens_after_the_decision_and_only_the_workspace_removes_a_worktree()
    {
        var rules = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "AcceptanceService.Rules.cs", StringComparison.Ordinal)));

        var service = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "AcceptanceService.cs", StringComparison.Ordinal)));

        // The decision is saved before cleanup is even called, in both routes.
        foreach (var route in service.Split("await _store.SaveChangesAsync(ct);").Skip(1))
        {
            Assert.Contains("ReleaseWorkspaceAsync", route, StringComparison.Ordinal);
        }

        Assert.True(
            service.IndexOf("await _store.SaveChangesAsync(ct);", StringComparison.Ordinal)
            < service.IndexOf("ReleaseWorkspaceAsync(", StringComparison.Ordinal),
            "the decision must be committed before any cleanup is attempted");

        // Nothing here removes anything itself; it asks a port.
        foreach (var construct in new[] { "\"worktree\"", "Directory.Delete", "File.Delete", "--force" })
        {
            Assert.False(rules.Contains(construct, StringComparison.Ordinal), construct);
            Assert.False(service.Contains(construct, StringComparison.Ordinal), construct);
        }

        // And it never offers an execution with no commit for release.
        Assert.Contains("WorkspaceCleanupOutcome.DeniedNoResultCommit", rules, StringComparison.Ordinal);
        Assert.Contains("execution.ResultCommitSha is null", rules, StringComparison.Ordinal);
    }

    private static Finding Finding(FindingSeverity severity, bool outOfScope = false) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            severity,
            FindingCategory.Correctness,
            "A concern",
            "It would break.",
            "src/thing.cs",
            1,
            null,
            FindingConfidence.High,
            outOfScope,
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
