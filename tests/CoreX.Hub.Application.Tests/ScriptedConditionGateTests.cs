using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.Application.Gates;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The conditions the Phase 5A scripted runtime produces, recognised by the Phase 5B
/// gates - end to end through a real repository, a real workspace and a real capture, but
/// without any execution lifecycle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScriptedConditionGateTests
{
    private static readonly string[] Allowed = ["src/**"];

    [Fact]
    public async Task An_unexpected_commit_is_recognised_by_the_runtime_policy_gate()
    {
        using var fixture = GateScenarioFixture.Create();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Describe());

        var run = await fixture.RunAgentAsync(prepared.Workspace!, ScriptedAgentMode.UnexpectedCommit);
        Assert.Equal(AgentRunOutcome.Completed, run.Outcome);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);

        // Evidence exists before any gate is consulted.
        Assert.NotEqual(capture.BaseCommitSha, capture.CurrentHeadSha);
        Assert.NotEmpty(capture.UnexpectedCommitShas);
        Assert.NotEmpty(capture.GitCommitGraph);

        var verdict = RuntimePolicyGate.Evaluate(GateScenarioFixture.ToGateInput(capture));

        Assert.True(verdict.Blocked);
        Assert.True(verdict.UnexpectedAgentCommitDetected);
        Assert.Contains(RuntimePolicyGate.UnexpectedAgentCommit, verdict.Reason, StringComparison.Ordinal);

        // The agent's commit is still exactly where it left it: nothing repaired it.
        var head = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        Assert.Equal(capture.CurrentHeadSha, head.CurrentHeadSha);
    }

    [Fact]
    public async Task An_out_of_scope_write_is_recognised_by_the_scope_gate()
    {
        using var fixture = GateScenarioFixture.Create();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.RunAgentAsync(prepared.Workspace!, ScriptedAgentMode.OutOfScopeWrite);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        var changes = GateScenarioFixture.ToGateInput(capture);

        Assert.Contains("infra/deploy.yaml", changes.ChangedPaths);

        var verdict = ScopeGuard.Evaluate(changes, Allowed, []);

        Assert.True(verdict.Blocked);
        Assert.Contains(verdict.ScopeEscapes, e => e.RelativePath == "infra/deploy.yaml");

        // The in scope file is not reported as an escape.
        Assert.DoesNotContain(verdict.ScopeEscapes, e => e.RelativePath.StartsWith("src/", StringComparison.Ordinal));

        // K1 passed, so the blocking gate really is K2.
        var result = GateFixtures.Gates.Evaluate(changes, Allowed, []);
        Assert.Equal(GateKind.Scope, result.BlockingVerdict!.Kind);
    }

    [Fact]
    public async Task A_secret_write_is_recognised_by_the_secret_gate()
    {
        using var fixture = GateScenarioFixture.Create();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.RunAgentAsync(prepared.Workspace!, ScriptedAgentMode.SecretWrite);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        var changes = GateScenarioFixture.ToGateInput(capture);

        // The added line really did carry the synthetic secret into the diff.
        Assert.Contains(changes.AddedLines, l => l.Text.Contains(SyntheticSecrets.OpenAiToken, StringComparison.Ordinal));

        var verdict = GateFixtures.SecretGuard.Evaluate(changes);

        Assert.True(verdict.Blocked);
        Assert.Contains(verdict.SecretHits, h => h.PatternName == "OPENAI_TOKEN");
        Assert.Contains(verdict.SecretHits, h => h.RelativePath == "src/config.txt");

        var result = GateFixtures.Gates.Evaluate(changes, Allowed, []);
        Assert.Equal(GateKind.Secret, result.BlockingVerdict!.Kind);
    }

    [Fact]
    public async Task An_in_scope_change_with_no_secret_is_eligible_for_a_result_commit()
    {
        using var fixture = GateScenarioFixture.Create();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.RunAgentAsync(prepared.Workspace!, ScriptedAgentMode.ApplyPatch);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        var result = GateFixtures.Gates.Evaluate(GateScenarioFixture.ToGateInput(capture), Allowed, []);

        Assert.True(result.EligibleForResultCommit);

        // Eligibility is all the evaluator did. HEAD has not moved: no commit was created.
        var afterwards = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        Assert.Equal(capture.BaseCommitSha, afterwards.CurrentHeadSha);
    }

    [Fact]
    public async Task A_blocked_secret_never_reaches_the_evidence_store_unredacted()
    {
        using var fixture = GateScenarioFixture.Create();

        var prepared = await fixture.Manager.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.RunAgentAsync(prepared.Workspace!, ScriptedAgentMode.SecretWrite);

        var capture = await fixture.Manager.CaptureChangesAsync(prepared.Workspace!, CancellationToken.None);
        var changes = GateScenarioFixture.ToGateInput(capture);

        // The gate sees the raw added line in memory and blocks on it...
        Assert.True(GateFixtures.SecretGuard.Evaluate(changes).Blocked);

        // ...and the same diff, when persisted, goes through redaction first.
        var descriptor = await fixture.EvidenceStore.WriteTextAsync(
            capture.GitWorktreeDiff, EvidenceKind.GitWorktreeDiff, "text/plain", CancellationToken.None);

        Assert.True(descriptor.RedactionCount > 0);

        foreach (var blob in fixture.BlobFiles())
        {
            var stored = await File.ReadAllTextAsync(blob);

            foreach (var secret in SyntheticSecrets.AllSecretValues())
            {
                Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
            }

            Assert.Contains("[REDACTED:OPENAI_TOKEN]", stored, StringComparison.Ordinal);
        }
    }
}
