using System.Runtime.Versioning;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Scripted;
using CoreX.Hub.ProcessFixture;

namespace CoreX.Hub.Agents.Tests;

/// <summary>
/// The eight deterministic modes, each producing a condition and none of them deciding
/// what that condition means.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScriptedAgentRuntimeTests
{
    [Fact]
    public async Task No_op_succeeds_and_changes_nothing()
    {
        using var fixture = ScriptedFixture.Create();
        var sink = new EventCollector();

        var result = await fixture.Runtime(ScriptedAgentMode.NoOp)
            .RunAsync(fixture.Request(), sink, CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(fixture.WorkspaceFiles());

        Assert.Equal(AgentEventKind.Started, sink.Events[0].Kind);
        Assert.Equal(AgentEventKind.Completed, sink.Events[^1].Kind);
    }

    [Fact]
    public async Task Apply_patch_writes_the_deterministic_change_and_succeeds()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.ApplyPatch)
            .RunAsync(fixture.Request(), new EventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.Equal(["src/feature.txt"], fixture.WorkspaceFiles());
        Assert.Contains("deterministic change", fixture.ReadWorkspaceFile("src/feature.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_of_scope_write_creates_the_condition_without_judging_it()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.OutOfScopeWrite)
            .RunAsync(fixture.Request(), new EventCollector(), CancellationToken.None);

        // Inside the workspace, outside a plausible allowed scope.
        Assert.Equal(["infra/deploy.yaml", "src/feature.txt"], fixture.WorkspaceFiles());

        // The runtime reports a completed run. Whether that is a scope violation is the
        // Phase 5B gate's decision, and this result contains no such verdict.
        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.DoesNotContain("scope", result.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Secret_write_creates_the_condition_without_judging_it()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.SecretWrite)
            .RunAsync(fixture.Request(), new EventCollector(), CancellationToken.None);

        Assert.Equal(["src/config.txt"], fixture.WorkspaceFiles());
        Assert.Contains(
            SyntheticSecrets.OpenAiToken,
            fixture.ReadWorkspaceFile("src/config.txt"),
            StringComparison.Ordinal);

        // Again: a condition, not a verdict. SecretGuard is Phase 5B.
        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.DoesNotContain("blocked", result.Diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_write_keeps_what_it_wrote_and_then_fails()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.PartialWrite)
            .RunAsync(fixture.Request(), new EventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);

        // The partial state survives: nothing is rolled back, because later evidence
        // collection has to be able to see exactly what happened.
        Assert.Equal(["src/step-one.txt"], fixture.WorkspaceFiles());
        Assert.Contains("could not be completed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Crash_is_distinguishable_from_a_failure()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.Crash)
            .RunAsync(fixture.Request(), new EventCollector(), CancellationToken.None);

        // The child died rather than exiting, which is a crash and not merely a non zero exit.
        Assert.Equal(AgentRunOutcome.Crashed, result.Outcome);
        Assert.NotNull(result.ExitCode);
        Assert.True(result.ExitCode < 0, $"expected an abnormal exit code but saw {result.ExitCode}");
    }

    [Fact]
    public async Task Hang_times_out_through_the_process_port()
    {
        using var fixture = ScriptedFixture.Create();

        var result = await fixture.Runtime(ScriptedAgentMode.Hang)
            .RunAsync(fixture.Request(hardTimeout: TimeSpan.FromSeconds(2)), new EventCollector(), CancellationToken.None);

        Assert.Equal(AgentRunOutcome.TimedOut, result.Outcome);
        Assert.True(result.Duration < TimeSpan.FromSeconds(30), $"the run took {result.Duration}");
    }

    [Fact]
    public async Task Hang_is_cancellable()
    {
        using var fixture = ScriptedFixture.Create();
        using var cancellation = new CancellationTokenSource();

        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        var result = await fixture.Runtime(ScriptedAgentMode.Hang)
            .RunAsync(fixture.Request(hardTimeout: TimeSpan.FromMinutes(2)), new EventCollector(), cancellation.Token);

        Assert.Equal(AgentRunOutcome.Cancelled, result.Outcome);
        Assert.True(result.Duration < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_structured_result_is_produced_when_one_is_requested()
    {
        using var fixture = ScriptedFixture.Create();
        var path = Path.Combine(fixture.WorkspaceRoot, "result.json");
        var sink = new EventCollector();

        var result = await fixture.Runtime(ScriptedAgentMode.NoOp)
            .RunAsync(fixture.Request(structuredOutputPath: path), sink, CancellationToken.None);

        Assert.Equal(AgentRunOutcome.Completed, result.Outcome);
        Assert.NotNull(result.StructuredOutput);
        Assert.Contains("\"runtime\":\"scripted\"", result.StructuredOutput!, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.Kind == AgentEventKind.StructuredOutputAvailable);
    }
}
