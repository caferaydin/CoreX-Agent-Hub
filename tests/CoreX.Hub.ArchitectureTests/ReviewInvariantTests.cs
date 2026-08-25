using System.Text.RegularExpressions;
using CoreX.Hub.Application.Review;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// What the independent review may and may not be, expressed as shape.
///
/// Behaviour is proved by the review and orchestration tests. These guard the properties a
/// behaviour test cannot see going away: that the review has no way to reach git, no way to
/// change anything, no second severity rule, and no path by which a reviewer's own claim
/// becomes the Hub's answer. Each of them could be undone by an ordinary looking edit, and
/// each of them is the reason the review is worth having.
/// </summary>
public sealed class ReviewInvariantTests
{
    [Fact]
    public void The_review_builds_no_git_command_of_its_own()
    {
        // E.1: git belongs to the workspace module. The engine asks for a tree at a commit
        // and asks for it to go away; a git verb appearing here would be a second place that
        // knows how to touch a repository.
        foreach (var file in ReviewSources())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            // Invocation shapes rather than English words: the reviewer prompt legitimately
            // says "a checkout of commit x", and a guard that failed on prose would be one
            // people learn to work around rather than one that means anything.
            foreach (var forbidden in new[]
                     {
                         "\"git\"", "rev-parse", "--porcelain", "GitCommandExecutor", "IGitCommandExecutor",
                         "\"worktree\"", "\"checkout\"", "\"status\"", "\"commit\"", "\"reset\"", "\"clean\"",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_review_starts_no_process_of_its_own()
    {
        // The reviewer goes through the accepted agent process boundary, which owns the job
        // object, the three deadlines and the deny by default environment. A second launch
        // point would be a subprocess none of that applies to.
        foreach (var file in ReviewSources().Concat([Gateways()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[] { "Process.Start", "ProcessStartInfo", "cmd.exe", "powershell" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_reviewer_never_decides_scope()
    {
        var engine = CodeOnly(File.ReadAllText(Source("ReviewEngine.cs")));

        // The answer is computed from what the execution changed, and the reviewer's claim is
        // only ever compared against it. A reviewer that owned this field could exempt its
        // own blocking finding.
        Assert.Contains("request.ChangedPaths", engine, StringComparison.Ordinal);
        Assert.Contains("OutOfScope = outOfScope", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("OutOfScope = reported.OutOfScope", engine, StringComparison.Ordinal);

        // And the disagreement is recorded rather than silently corrected.
        Assert.Contains("disagreements.Add", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reviewer_never_decides_the_outcome()
    {
        // There is no verdict field to set, and prose is never read: the outcome comes from
        // the frozen gate over canonical findings.
        var engine = CodeOnly(File.ReadAllText(Source("ReviewEngine.cs")));

        Assert.Contains("ReviewFindingGate.HasBlockingFindings", engine, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "MajorThreshold", "FindingSeverity.Critical", "FindingSeverity.Major" })
        {
            Assert.False(engine.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        foreach (var word in new[] { "verdict", "approved", "blocking", "score", "shouldMerge" })
        {
            Assert.DoesNotContain(word, ReviewSchema.Json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_provider_that_finished_is_not_treated_as_a_review()
    {
        var engine = CodeOnly(File.ReadAllText(Source("ReviewEngine.cs")));

        // Measured in Phase 7A: the provider can exit zero, report success and return no
        // structured output. Only the presence of that document discriminates, so the code
        // reads it and refuses when it is not there.
        Assert.Contains("ReviewSchema.Read(run.StructuredOutput)", engine, StringComparison.Ordinal);
        Assert.Contains("if (!document.IsValid)", engine, StringComparison.Ordinal);

        // And the outcome of an unreadable review is never Clean.
        var unusable = Between(engine, "private static ExecutionReviewResult Unusable(", "SessionId(Guid");

        Assert.Contains("ReviewVerdict.Unusable", unusable, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewVerdict.Clean", unusable, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retry_is_bounded_and_only_for_a_document_that_did_not_arrive()
    {
        Assert.Equal(2, ReviewEngine.MaximumAttempts);

        var engine = CodeOnly(File.ReadAllText(Source("ReviewEngine.cs")));

        // A provider that failed, timed out or was cancelled produced nothing to validate,
        // and none of those is a schema problem. The only `continue` in the loop is the one
        // that follows an unreadable document.
        Assert.Single(Regex.Matches(engine, @"^\s+continue;\s*$", RegexOptions.Multiline));

        var fatal = Between(engine, "Fatal(ReviewRuntimeResult run)", "private static string? Tampered");

        foreach (var outcome in new[] { "TimedOut", "Cancelled" })
        {
            Assert.Contains(outcome, fatal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_tree_is_checked_before_the_reviewer_runs_and_after_every_attempt()
    {
        var engine = CodeOnly(File.ReadAllText(Source("ReviewEngine.cs")));

        var loop = Between(engine, "for (var attempt = 1;", "return Judge(");

        Assert.Contains("await _workspace.InspectAsync(snapshot, ct)", loop, StringComparison.Ordinal);
        Assert.Contains("Tampered(before, after)", loop, StringComparison.Ordinal);

        // And tampering is not retried: it is a review of something that changed underneath
        // it, which no second attempt can repair.
        var tampered = Between(loop, "if (Tampered(before, after)", "if (Fatal(run)");
        Assert.Contains("return Unusable", tampered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reviewer_contract_belongs_to_the_hub_and_not_to_the_repository_under_review()
    {
        var gateways = File.ReadAllText(Gateways());

        // Written by composition, into the Hub's own data directory, on every run - so an
        // edited copy on disk cannot quietly become the contract, and nothing inside the
        // reviewed tree is ever read as instruction.
        Assert.Contains("File.WriteAllText(path, Markdown", gateways, StringComparison.Ordinal);
        Assert.Contains("Untrusted content", gateways, StringComparison.Ordinal);

        foreach (var file in ReviewSources().Concat([Gateways()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[] { "CLAUDE.md", "AGENTS.md", ".claude", ".cursorrules" })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }
    }

    [Fact]
    public void The_review_repairs_nothing()
    {
        // Phase 7 reviews. Automatic repair is a later decision, and a review that could
        // change the thing it reviewed would stop being a review of it.
        foreach (var file in ReviewSources().Concat([Gateways()]))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var forbidden in new[]
                     {
                         "WorkspaceWrite", "CreateResultCommit", "ApplyPatch", "File.Delete", "Directory.Delete",
                     })
            {
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{name}: {forbidden}");
            }
        }

        // Nothing in the Application half writes a file at all.
        foreach (var file in ReviewSources())
        {
            Assert.DoesNotContain("File.Write", CodeOnly(File.ReadAllText(file)), StringComparison.Ordinal);
        }

        // Composition writes exactly one file, the Hub owned contract, and it writes it into
        // the Hub's own directory rather than anywhere a reviewer or an agent could reach.
        var gateways = CodeOnly(File.ReadAllText(Gateways()));

        Assert.Single(gateways.Split('\n'), l => l.Contains("File.Write", StringComparison.Ordinal));
        Assert.Contains("File.WriteAllText(path, Markdown", gateways, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_evidence_is_written_against_the_review_step()
    {
        var review = CodeOnly(File.ReadAllText(
            Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Orchestration",
                "ExecutionOrchestrator.Review.cs")));

        // Otherwise an execution would hold two AgentStdout artifacts with nothing to say
        // which of them anybody was reading.
        Assert.Contains("EvidenceKind.AgentStdout, \"text/plain\", ct, stepId", review, StringComparison.Ordinal);
        Assert.Contains("EvidenceKind.ReviewFindings", review, StringComparison.Ordinal);

        // And the findings are written before the outcome that cites them. The last
        // CompleteAsync is the one that ends a review that actually ran; the earlier one
        // belongs to the branch where there was nothing to anchor to.
        var index = review.IndexOf("PersistFindingsAsync(execution, step", StringComparison.Ordinal);
        var completed = review.LastIndexOf("return await CompleteAsync(", StringComparison.Ordinal);

        Assert.True(index > 0 && completed > index, "findings must be persisted before the execution completes");
    }

    [Fact]
    public void Which_profile_reviews_is_a_catalogue_answer_rather_than_a_coded_one()
    {
        var composition = CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Cli", "Orchestration", "ExecutionComposition.cs")));

        // The reviewer is resolved from a named profile, exactly as the implementer's runtime
        // is. Naming a provider here would make swapping the reviewer a code change.
        Assert.Contains("AgentRuntimeRegistry.EnsureResolvable(runtimes, profile)", composition, StringComparison.Ordinal);
        Assert.Contains("AgentRole.Reviewer", composition, StringComparison.Ordinal);
        Assert.Contains("SupportsReadOnlyMode", composition, StringComparison.Ordinal);
        Assert.Contains("SupportsStructuredSchema", composition, StringComparison.Ordinal);

        foreach (var provider in new[] { "ClaudeCodeRuntime", "Codex", "Gemini", "Anthropic" })
        {
            Assert.False(composition.Contains(provider, StringComparison.Ordinal), provider);
        }
    }

    [Fact]
    public void An_unreviewed_result_is_never_recovered_into_a_pass()
    {
        var recovery = CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Application", "Orchestration", "PostCommitRecoveryService.cs")));

        // Recovery holds no reviewer and no workspace, so the only honest thing it can do
        // with a repository that is reviewed is refuse to call an unreviewed result Passed.
        Assert.Contains("_reviewRequired", recovery, StringComparison.Ordinal);
        Assert.Contains("ExecutionOutcome.ReviewUnusable", recovery, StringComparison.Ordinal);
    }

    [Fact]
    public void A_review_that_already_happened_is_read_back_rather_than_assumed_clean()
    {
        var recovery = CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Application", "Orchestration", "PostCommitRecoveryService.cs")));

        // A host can die between the review and the row that records its verdict. The
        // findings are already durable then, and reading them is the only answer that agrees
        // with what the pipeline would have written - assuming a pass would accept work a
        // reviewer had already blocked.
        Assert.Contains("await store.ListFindingsAsync(execution.Id, ct)", recovery, StringComparison.Ordinal);
        Assert.Contains("ReviewFindingGate.HasBlockingFindings(findings)", recovery, StringComparison.Ordinal);
        Assert.Contains("ExecutionOutcome.ReviewBlocked", recovery, StringComparison.Ordinal);

        // And it asks the gate rather than restating what blocks.
        foreach (var forbidden in new[] { "MajorThreshold", "FindingSeverity" })
        {
            Assert.False(recovery.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }

        // Nothing here can obtain a review: there is no provider, no workspace and no engine.
        foreach (var forbidden in new[] { "IExecutionReviewer", "ReviewEngine", "IReviewWorkspace", "_agent" })
        {
            Assert.False(recovery.Contains(forbidden, StringComparison.Ordinal), forbidden);
        }
    }

    [Fact]
    public void Both_producers_of_the_same_evidence_kind_name_the_step_they_belong_to()
    {
        var orchestration = Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Orchestration");

        var agent = CodeOnly(File.ReadAllText(Path.Combine(orchestration, "ExecutionOrchestrator.Evidence.cs")));
        var review = CodeOnly(File.ReadAllText(Path.Combine(orchestration, "ExecutionOrchestrator.Review.cs")));

        // Since the review became real, AgentStdout has two authors. "The one with no step"
        // is not an answer to which of them a reader is looking at.
        Assert.Contains("FindStepAsync(execution.Id, ExecutionStepKind.Agent, ct)", agent, StringComparison.Ordinal);
        Assert.Contains("EvidenceKind.AgentStdout, \"text/plain\", ct, step?.Id", agent, StringComparison.Ordinal);
        Assert.Contains("EvidenceKind.AgentStdout, \"text/plain\", ct, stepId", review, StringComparison.Ordinal);

        // And the operator surface says so rather than leaving the reader to guess.
        var evidence = CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Cli", "Commands", "EvidenceCommands.cs")));

        Assert.Contains("\"STEP\"", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_review_says_why_it_was_skipped()
    {
        var gates = CodeOnly(File.ReadAllText(Path.Combine(
            RepoLayout.SourceRoot, "CoreX.Hub.Application", "Orchestration", "ExecutionOrchestrator.Gates.cs")));

        // Every skip names its own cause. "Not configured" is a claim about the repository,
        // and on one that has a reviewer it is false - so it is used only where it is true.
        var notConfigured = gates.Split('\n')
            .Count(l => l.Contains("ExecutionStepKind.Review, ReviewNotConfigured", StringComparison.Ordinal));

        Assert.Equal(1, notConfigured);

        foreach (var reason in new[]
                 {
                     "ExecutionStepKind.Review, \"blocked by \" + blocked.Kind",
                     "ExecutionStepKind.Review, CancelledBeforeCommit",
                     "ExecutionStepKind.Review, \"no result commit\"",
                     "ExecutionStepKind.Review, WorktreeNotAtResultCommit",
                     "ExecutionStepKind.Review, ReviewSkippedVerificationFailed",
                 })
        {
            Assert.Contains(reason, gates, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ReviewSources() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Review"), "*.cs");

    private static string Source(string fileName) =>
        Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Application", "Review", fileName);

    private static string Gateways() =>
        Path.Combine(RepoLayout.SourceRoot, "CoreX.Hub.Cli", "Orchestration", "ReviewGateways.cs");

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
