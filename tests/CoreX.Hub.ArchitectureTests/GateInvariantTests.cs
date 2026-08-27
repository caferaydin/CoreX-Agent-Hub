using System.Reflection;
using CoreX.Hub.Application.Gates;
using System.Text.RegularExpressions;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Phase 5B structural guards. The gates are application policy over already captured
/// machine facts, and they stay that way.
/// </summary>
public sealed class GateInvariantTests
{
    private const string ContractFileName = "HubSystemContract.cs";

    private static IEnumerable<string> GateSourceFiles() =>
        RepoLayout.ProductionSourceFiles.Where(f =>
            f.Contains(
                Path.DirectorySeparatorChar + "CoreX.Hub.Application" + Path.DirectorySeparatorChar + "Gates" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));

    [Fact]
    public void The_gates_exist_and_are_scanned()
    {
        // A guard that inspected nothing would pass for the wrong reason.
        var names = GateSourceFiles().Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Contains("RuntimePolicyGate.cs", names);
        Assert.Contains("ScopeGuard.cs", names);
        Assert.Contains("SecretGuard.cs", names);
    }

    [Fact]
    public void The_gates_run_no_process_no_git_and_no_filesystem()
    {
        // They judge captured facts. Running git or reading the disk here would duplicate
        // Phase 3 and put a second copy of the truth in the system.
        string[] forbidden =
        [
            "Process.Start", "ProcessStartInfo", "git ", "\"git\"",
            "File.", "Directory.", "StreamReader", "StreamWriter",
            "DbContext", "SqlConnection", "HttpClient",
        ];

        foreach (var file in GateSourceFiles())
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
    public void The_gates_persist_no_evidence()
    {
        foreach (var file in GateSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in new[] { "EvidenceBlobStore", "WriteTextAsync", "Blob", "Sha256" })
            {
                Assert.False(code.Contains(construct, StringComparison.Ordinal), $"{name}: {construct}");
            }
        }
    }

    [Fact]
    public void The_gates_name_no_provider()
    {
        // A local harness and a hosted assistant are judged by exactly the same gates.
        foreach (var file in GateSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var word in new[] { "Claude", "Codex", "Gemini", "Qwen", "Anthropic", "OpenAI", "Ollama" })
            {
                Assert.False(code.Contains(word, StringComparison.OrdinalIgnoreCase), $"{name}: {word}");
            }
        }
    }

    [Fact]
    public void There_is_exactly_one_secret_pattern_catalog()
    {
        // SecretGuard reaches the Phase 4 catalog through a port rather than owning a
        // second regex set: a gate and a redactor that disagreed about what a secret looks
        // like would be worse than either alone.
        var scanner = new CatalogSecretScanner();

        Assert.Equal(SecretPatternCatalog.Names, scanner.PatternNames);
        Assert.Equal(SecretPatternCatalog.Names, new SecretGuard(scanner).PatternNames);

        // The gate layer owns no expression of its own at all now that scope matching
        // belongs to the domain value object.
        foreach (var fileName in new[] { "SecretGuard.cs", "ISecretScanner.cs" })
        {
            var file = GateSourceFiles().Single(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));
            var code = CodeOnly(File.ReadAllText(file));

            Assert.False(code.Contains("Regex", StringComparison.Ordinal), $"{fileName} declares its own expression");
            Assert.False(code.Contains("SecretPatternCatalog", StringComparison.Ordinal), $"{fileName} bypasses the port");
        }
    }

    [Fact]
    public void No_history_repair_path_was_introduced()
    {
        // The Hub reports an unexpected commit and leaves it exactly where the agent put
        // it. E.1 #9 already forbids this across src; this asserts the new code too.
        string[] repairs =
        [
            "reset --hard", "branch -f", "update-ref", "commit --amend",
            "rebase", "push --force", "--force-with-lease",
        ];

        // The Hub execution contract is exempt and proven so below: it names these
        // operations in order to forbid the agent from performing them, which is the
        // opposite of a repair path.
        var contract = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), ContractFileName, StringComparison.Ordinal));

        foreach (var file in RepoLayout.ProductionSourceFiles.Where(f => f != contract))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var repair in repairs)
            {
                Assert.False(code.Contains(repair, StringComparison.OrdinalIgnoreCase), $"{name}: {repair}");
            }
        }

        // Non vacuous exemption: the contract really does forbid these, and really does
        // run nothing.
        var contractText = File.ReadAllText(contract);
        Assert.Contains("Do not push, merge, rebase", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("Process", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("GitCommand", contractText, StringComparison.Ordinal);
    }

    [Fact]
    public void No_generic_policy_engine_was_introduced()
    {
        // Three fixed product gates, not a framework. No rule language, no dynamic
        // registration, no chain of responsibility, no plugin surface.
        string[] frameworkish =
        [
            "IPolicy", "PolicyEngine", "IRule", "RuleEngine", "IGateHandler",
            "IGate", "GateRegistry", "ChainOfResponsibility", "Middleware",
        ];

        foreach (var file in GateSourceFiles())
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var construct in frameworkish)
            {
                Assert.False(code.Contains(construct, StringComparison.Ordinal), $"{name}: {construct}");
            }
        }

        // Exactly three kinds, and no way to add a fourth at runtime.
        Assert.Equal(3, Enum.GetValues<GateKind>().Length);
    }

    [Fact]
    public void The_gate_layer_changes_no_aggregate_state()
    {
        // It returns verdicts. It never reaches for an execution, a task or an acceptance
        // decision, so Phase 5C can own every transition without contending with it.
        foreach (var type in new[]
                 {
                     typeof(RuntimePolicyGate), typeof(ScopeGuard),
                     typeof(SecretGuard), typeof(ExecutionSafetyGates),
                 })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var parameter in method.GetParameters())
                {
                    var parameterType = parameter.ParameterType.Name;

                    Assert.DoesNotContain("HubTask", parameterType, StringComparison.Ordinal);
                    Assert.DoesNotContain("Execution", parameterType, StringComparison.Ordinal);
                    Assert.DoesNotContain("Acceptance", parameterType, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Task_scope_matching_lives_only_in_the_domain_value_object()
    {
        // Architecture D.4 names ScopeGlob a domain value object. One authority decides
        // what a scope entry may be, canonically is and matches; a second matcher
        // elsewhere would let a task be judged by rules other than the ones it was
        // validated against.
        var authority = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), "ScopeGlob.cs", StringComparison.Ordinal));

        Assert.Contains(
            Path.DirectorySeparatorChar + "CoreX.Hub.Domain" + Path.DirectorySeparatorChar,
            authority,
            StringComparison.Ordinal);

        // Non vacuous: the authority really is the thing that translates a glob.
        var authorityCode = File.ReadAllText(authority);
        Assert.Contains("Regex", authorityCode, StringComparison.Ordinal);
        Assert.Contains("Matches", authorityCode, StringComparison.Ordinal);

        // Names that would be a second task scope matcher. Deliberately narrow: this bans
        // Hub task scope semantics elsewhere, not glob functionality in general.
        string[] rivals = ["ScopePattern", "GlobMatcher", "ScopeRegexMatcher", "ScopeMatcher", "GlobToRegex"];

        foreach (var file in RepoLayout.ProductionSourceFiles.Where(f => f != authority))
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var rival in rivals)
            {
                Assert.False(code.Contains(rival, StringComparison.Ordinal), $"{name}: {rival}");
            }
        }
    }

    [Fact]
    public void The_scope_gate_delegates_rather_than_translating_globs_itself()
    {
        var guard = RepoLayout.ProductionSourceFiles
            .Single(f => string.Equals(Path.GetFileName(f), "ScopeGuard.cs", StringComparison.Ordinal));

        var code = CodeOnly(File.ReadAllText(guard));

        Assert.Contains("ScopeGlob.Parse", code, StringComparison.Ordinal);
        Assert.Contains("ScopeGlob.NormalizePath", code, StringComparison.Ordinal);

        // No glob translation of its own: no expression, no wildcard handling.
        Assert.DoesNotContain("Regex", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Translate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StringBuilder", code, StringComparison.Ordinal);
    }

    /// <summary>Reads one production source file by name. Ambiguity is a failure, not a first match.</summary>
    private static string Source(string fileName) =>
        File.ReadAllText(Assert.Single(
            RepoLayout.ProductionSourceFiles,
            f => Path.GetFileName(f).Equals(fileName, StringComparison.Ordinal)));

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // -- how long the catalogue may look --------------------------------------------------------

    /// <summary>
    /// The linear regex engine is not used by anything on the secret path.
    ///
    /// Not a style preference. On this runtime <c>NonBacktracking</c> combined with a finite match
    /// timeout was measured returning no matches at all - silently, without throwing - for several
    /// of these shapes once the input passed about a mebibyte. A secret in a large artifact would
    /// have gone unredacted and unreported by the gate, so the faster engine is unavailable here at
    /// any price, and the behaviour that would break is guarded by tests as well as by this.
    /// </summary>
    [Fact]
    public void The_secret_path_never_uses_the_linear_engine()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("NonBacktracking", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);

        // And every shape the catalogue holds really is built without it.
        foreach (var pattern in SecretPatternCatalog.Patterns)
        {
            Assert.False(
                pattern.Compile(TimeSpan.FromSeconds(1)).Options.HasFlag(RegexOptions.NonBacktracking));
        }
    }

    /// <summary>
    /// Nothing on the secret path is allowed to run without a bound.
    ///
    /// An unbounded match is the failure the budget exists to prevent, and it is one line away at
    /// all times.
    /// </summary>
    [Fact]
    public void The_secret_path_never_runs_unbounded()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("InfiniteMatchTimeout", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);

        foreach (var pattern in SecretPatternCatalog.Patterns)
        {
            Assert.NotEqual(
                Regex.InfiniteMatchTimeout, pattern.Compile(TimeSpan.FromSeconds(1)).MatchTimeout);
        }
    }

    /// <summary>
    /// The budget scales with the input and stops, in both directions.
    ///
    /// A flat number cannot separate a large legitimate artifact from a small hostile one: it is
    /// either too small for the first or far too generous for the second. The previous two seconds
    /// was the first of those, and sixty would have been the second - seven patterns entitled to a
    /// minute apiece for one artifact.
    /// </summary>
    [Fact]
    public void The_budget_is_size_aware_and_finite()
    {
        Assert.True(SecretBudget.PerPattern(256 * 1024) < SecretBudget.PerPattern(16 * 1024 * 1024));

        Assert.Equal(SecretBudget.PerPatternMaximum, SecretBudget.PerPattern(int.MaxValue));
        Assert.True(SecretBudget.PerPatternMaximum <= TimeSpan.FromSeconds(8));
        Assert.True(SecretBudget.OperationMaximum <= TimeSpan.FromSeconds(12));

        // No pattern is entitled to a flat two seconds any more, and no artifact to seven of them.
        Assert.NotEqual(TimeSpan.FromSeconds(2), SecretBudget.PerPattern(16 * 1024 * 1024));

        Assert.True(
            SecretBudget.OperationMaximum
            < SecretBudget.PerPatternMaximum * SecretPatternCatalog.Patterns.Count);
    }

    /// <summary>
    /// One budget authority, used by the redactor and by the gate.
    ///
    /// The catalogue is shared so the two cannot disagree about what a secret is; the budget is
    /// shared so they cannot disagree about how long it may be looked for.
    /// </summary>
    [Fact]
    public void The_redactor_and_the_scanner_share_one_budget_authority()
    {
        foreach (var fileName in new[] { "EvidenceRedactor.cs", "CatalogSecretScanner.cs" })
        {
            var code = CodeOnly(Source(fileName));

            Assert.Contains("SecretBudget.Open(text.Length)", code, StringComparison.Ordinal);
            Assert.Contains("budget.Next(pattern)", code, StringComparison.Ordinal);

            // No local timeout, no second policy, and nothing that could skip a pattern.
            Assert.DoesNotContain("TimeSpan.From", code, StringComparison.Ordinal);
            Assert.DoesNotContain("catch", code, StringComparison.Ordinal);
            Assert.DoesNotContain("continue", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A timeout is never swallowed anywhere between the catalogue and persistence.
    ///
    /// The direction of this failure is the whole point: a scan that did not finish and a diff with
    /// nothing in it are opposite answers, and a gate that confused them would pass exactly the
    /// change it exists to stop.
    /// </summary>
    [Fact]
    public void A_timeout_is_never_swallowed()
    {
        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("RegexMatchTimeoutException", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        // One file names it, and only to throw it.
        Assert.Equal(["SecretBudget.cs"], offenders);

        Assert.Contains(
            "throw new RegexMatchTimeoutException", CodeOnly(Source("SecretBudget.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The budget is a product constant, not something an artifact can ask to change.
    ///
    /// An organization, a task or a provider that could widen it would be able to ask to be
    /// examined less carefully.
    /// </summary>
    [Fact]
    public void The_budget_is_not_configuration()
    {
        var budget = CodeOnly(Source("SecretBudget.cs"));

        foreach (var configurable in new[] { "IHubConfig", "Config", "Options.Get", "Environment.GetEnvironmentVariable" })
        {
            Assert.DoesNotContain(configurable, budget, StringComparison.Ordinal);
        }

        var offenders = RepoLayout.ProductionSourceFiles
            .Where(f => f.Contains($"{Path.DirectorySeparatorChar}Configuration{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => CodeOnly(File.ReadAllText(f)).Contains("Budget", StringComparison.Ordinal))
            .Select(RepoLayout.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The isolated evidence path and the ordinary one share the budget, without a branch.
    ///
    /// A policy that depended on which side of the boundary an artifact was stored on would be two
    /// definitions of how carefully evidence is examined.
    /// </summary>
    [Fact]
    public void The_isolated_and_ordinary_paths_share_one_policy()
    {
        foreach (var fileName in new[] { "HardEvidenceGateway.cs", "EvidenceBlobStore.cs" })
        {
            var code = CodeOnly(Source(fileName));

            Assert.Contains("EvidenceRedactor", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SecretBudget", code, StringComparison.Ordinal);
            Assert.DoesNotContain("MatchTimeout", code, StringComparison.Ordinal);
        }
    }
}
