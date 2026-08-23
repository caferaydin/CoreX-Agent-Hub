using System.Reflection;
using CoreX.Hub.Application.Gates;
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

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
