using System.Diagnostics;
using System.Runtime.Versioning;
using CoreX.Hub.Api;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// Starting a run from a host that has to stay up.
///
/// The property being proved is a negative one and it is the whole reason this phase has an
/// execution launcher at all: the HTTP request is not what owns the work. It returns as soon
/// as there is an identity to return, the run continues without it, and nothing a client does
/// to its connection reaches the agent.
///
/// No provider is called. The fixture's organization declares a deterministic scripted
/// runtime whose fixture executable does not exist, so a launch gets far enough to prove the
/// seam and then fails on its own terms rather than costing a model call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunStartTests
{
    [Fact]
    public async Task Starting_a_run_does_not_wait_for_the_run()
    {
        await using var hub = await HubApiFixture.CreateAsync(allowExecutionLaunch: true);

        await hub.SeedTaskAsync("API-400", TaskState.Ready);

        var answered = Stopwatch.StartNew();

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-400/runs",
            new { actor = "operator", agentProfile = "scripted-implementer" });

        answered.Stop();

        // Either it started, or it was refused for a reason the installation gave - what it
        // must never do is block the caller for the length of an execution.
        Assert.True(
            answered.Elapsed < TimeSpan.FromSeconds(60),
            $"the request took {answered.Elapsed}, which means it waited for the run");

        if (status == 202)
        {
            // Accepted, with something durable to follow.
            Assert.NotEqual(Guid.Empty, body.GetProperty("executionId").GetGuid());
            Assert.Equal("API-400", body.GetProperty("taskKey").GetString());
            Assert.Contains("/events", body.GetProperty("eventsUrl").GetString()!, StringComparison.Ordinal);
        }
        else
        {
            // Refused by an authority, in the ordinary envelope.
            Assert.True(status is 400 or 409, $"unexpected status {status}");
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()));
        }
    }

    [Fact]
    public async Task A_host_that_may_not_launch_refuses_before_composing_anything()
    {
        await using var hub = await HubApiFixture.CreateAsync(allowExecutionLaunch: false);

        await hub.SeedTaskAsync("API-401", TaskState.Ready);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-401/runs",
            new { actor = "operator", agentProfile = "scripted-implementer" });

        Assert.Equal(409, status);
        Assert.Equal("execution_refused", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_task_that_is_not_ready_is_refused_by_the_task_rules()
    {
        await using var hub = await HubApiFixture.CreateAsync(allowExecutionLaunch: true);

        await hub.SeedTaskAsync("API-402", TaskState.Draft);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-402/runs",
            new { actor = "operator", agentProfile = "scripted-implementer" });

        Assert.Equal(409, status);
        Assert.Contains("not Ready", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_profile_that_is_not_an_implementer_is_refused()
    {
        await using var hub = await HubApiFixture.CreateAsync(allowExecutionLaunch: true);

        await hub.SeedTaskAsync("API-403", TaskState.Ready);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-403/runs",
            new { actor = "operator", agentProfile = "nobody-declared-this" });

        Assert.Equal(409, status);
        Assert.Contains("not found", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_launcher_never_hands_a_request_token_to_the_orchestrator()
    {
        // The safety property, asserted as shape because it cannot be observed from outside:
        // a run started by this host is driven under the host's lifetime, and the token that
        // belongs to the HTTP request is only ever used for waiting on the identity.
        // Comments stripped: the file explains the anti-pattern by name, and a guard that
        // matched prose would fail on the sentence warning against the thing it forbids.
        var source = CodeOnly(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CoreX.Hub.Api", "ExecutionLauncher.cs")));

        Assert.Contains("_lifetime.Token", source, StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(_lifetime.Token)", source, StringComparison.Ordinal);

        // The request token has exactly one job, and it is not driving the run: it bounds
        // the wait for the identity and opens the session, both of which happen before
        // the hand-off. The method that actually drives the orchestrator never sees it.
        var from = source.IndexOf("private async Task DriveAsync", StringComparison.Ordinal);
        var to = source.IndexOf("private static async Task<Guid> AwaitIdentityAsync", StringComparison.Ordinal);

        Assert.True(from > 0 && to > from, "the launcher no longer has the two methods this guard reads");

        var driving = source[from..to];

        Assert.DoesNotContain("requestAborted", driving, StringComparison.Ordinal);
        Assert.Contains("stopping.Token", driving, StringComparison.Ordinal);

        // And the work is owned rather than dropped: no fire-and-forget anywhere.
        Assert.DoesNotContain("Task.Run(", source, StringComparison.Ordinal);
        Assert.Contains("IHostedService", source, StringComparison.Ordinal);
        Assert.Contains("_running", source, StringComparison.Ordinal);
    }

    [Fact]
    public void One_execution_at_a_time_is_still_the_machine_wide_rule()
    {
        var source = CodeOnly(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CoreX.Hub.Api", "ExecutionLauncher.cs")));

        // The same cross-process file handle the console claims, so a run started in a browser
        // and a run started at a terminal still cannot happen at once.
        Assert.Contains("MachineExecutionSlot", source, StringComparison.Ordinal);
        Assert.Contains("TryClaim()", source, StringComparison.Ordinal);

        // And composition is the shared planner, not a second copy of the rules.
        Assert.Contains("ExecutionLaunchPlanner.PlanAsync", source, StringComparison.Ordinal);
    }

    /// <summary>Source with the commentary removed, so a guard measures code.</summary>
    private static string CodeOnly(string source) =>
        string.Join(
            Environment.NewLine,
            source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreX.Hub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("no repository root");
    }
}
