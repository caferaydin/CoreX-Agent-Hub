using System.Reflection;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// Evidence is collected first and every gate is evaluated, but the outcome belongs to the
/// first gate that blocked, in K1, K2, K3 order.
/// </summary>
public sealed class GateOrderTests
{
    private static readonly string[] Allowed = ["src/**"];

    private static CapturedChanges Everything(bool moved, bool outOfScope, bool secret) =>
        GateFixtures.Changes(
            head: moved ? GateFixtures.Moved : GateFixtures.Base,
            unexpectedCommits: moved ? [GateFixtures.Moved] : [],
            changedPaths: outOfScope ? ["src/a.cs", "infra/deploy.yaml"] : ["src/a.cs"],
            addedLines: secret
                ? GateFixtures.Added("src/a.cs", "var key = \"" + SyntheticSecrets.OpenAiToken + "\";")
                : GateFixtures.Added("src/a.cs", "// ordinary work"));

    [Fact]
    public void All_three_failing_is_reported_as_a_runtime_policy_block()
    {
        var result = GateFixtures.Gates.Evaluate(Everything(true, true, true), Allowed, []);

        Assert.Equal(GateKind.RuntimePolicy, result.BlockingVerdict!.Kind);
        Assert.False(result.EligibleForResultCommit);

        // The later gates still ran, so their diagnostics are available. Collecting a
        // diagnostic is not the same as a gate winning.
        Assert.True(result.Verdict(GateKind.Scope).Blocked);
        Assert.True(result.Verdict(GateKind.Secret).Blocked);
        Assert.NotEmpty(result.Verdict(GateKind.Scope).ScopeEscapes);
        Assert.NotEmpty(result.Verdict(GateKind.Secret).SecretHits);
    }

    [Fact]
    public void Scope_wins_when_the_runtime_policy_gate_passed()
    {
        var result = GateFixtures.Gates.Evaluate(Everything(false, true, true), Allowed, []);

        Assert.Equal(GateKind.Scope, result.BlockingVerdict!.Kind);
        Assert.Equal(GateStatus.Passed, result.Verdict(GateKind.RuntimePolicy).Status);
        Assert.True(result.Verdict(GateKind.Secret).Blocked);
    }

    [Fact]
    public void Secret_wins_when_the_first_two_passed()
    {
        var result = GateFixtures.Gates.Evaluate(Everything(false, false, true), Allowed, []);

        Assert.Equal(GateKind.Secret, result.BlockingVerdict!.Kind);
        Assert.Equal(GateStatus.Passed, result.Verdict(GateKind.RuntimePolicy).Status);
        Assert.Equal(GateStatus.Passed, result.Verdict(GateKind.Scope).Status);
    }

    [Fact]
    public void All_gates_passing_makes_a_result_commit_eligible()
    {
        var result = GateFixtures.Gates.Evaluate(Everything(false, false, false), Allowed, []);

        Assert.Null(result.BlockingVerdict);
        Assert.True(result.EligibleForResultCommit);
        Assert.All(result.Verdicts, v => Assert.Equal(GateStatus.Passed, v.Status));
        Assert.Equal("all gates passed", result.Describe());
    }

    [Fact]
    public void The_evaluator_creates_no_commit_and_changes_no_state()
    {
        // Eligibility is a statement, not an action. The type exposes nothing that could
        // commit, release, transition or persist anything - Phase 5C owns all of that.
        var members = typeof(ExecutionSafetyGates).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        foreach (var forbidden in new[]
                 {
                     "Commit", "Release", "Transition", "Save", "Persist", "Accept",
                     "Run", "Start", "Verify", "Delete",
                 })
        {
            Assert.DoesNotContain(members, m => m.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("Evaluate", members);

        // And it is not named as if it orchestrated anything either.
        Assert.DoesNotContain("Orchestrator", typeof(ExecutionSafetyGates).Name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gates_are_three_fixed_products_rather_than_a_registry()
    {
        // No policy engine, no rule language, no dynamic registration: three kinds, and a
        // constructor that takes the one collaborator K3 genuinely needs.
        Assert.Equal(["RuntimePolicy", "Scope", "Secret"], Enum.GetNames<GateKind>().Order(StringComparer.Ordinal));

        var constructor = Assert.Single(typeof(ExecutionSafetyGates).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(SecretGuard), parameter.ParameterType);
    }

    [Fact]
    public void There_is_no_way_to_override_a_blocked_secret_gate()
    {
        // SecretGuardBlocked is machine truth. Nothing in the gate layer offers a bypass,
        // and a human decision later must not be able to rewrite it either.
        foreach (var type in new[]
                 {
                     typeof(SecretGuard), typeof(ExecutionSafetyGates),
                     typeof(GateEvaluationResult), typeof(GateVerdict),
                 })
        {
            foreach (var member in type.GetMembers().Select(m => m.Name))
            {
                foreach (var bypass in new[] { "Override", "Ignore", "Force", "Bypass", "Suppress", "Waive" })
                {
                    Assert.DoesNotContain(bypass, member, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
