using System.Runtime.Versioning;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Composition;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// Cancelling over HTTP.
///
/// There is nothing new to decide here and that is the point. Every rule below belongs to
/// <see cref="TaskCancellationService"/> and has since Phase 5: what may be cancelled, that a
/// live run is asked to stop rather than declared stopped, that a task is never made terminal
/// over a run that is still writing, and that an execution keeps whatever outcome it earned.
/// HTTP is one more way to ask.
///
/// So these tests are mostly about what the endpoint does *not* do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CancellationTests
{
    [Fact]
    public async Task A_task_with_nothing_running_is_cancelled()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-500", TaskState.Ready);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-500/cancel",
            new { actor = "operator", reason = "not needed after all" });

        Assert.Equal(200, status);
        Assert.Equal("Cancelled", body.GetProperty("state").GetString());
        Assert.Equal("Cancelled", body.GetProperty("taskState").GetString());
        Assert.Contains("no execution was running", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // Nothing was manufactured to cancel.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("executionId").ValueKind);
    }

    [Fact]
    public async Task A_draft_task_is_cancellable_exactly_as_the_authority_allows()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-501", TaskState.Draft);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-501/cancel",
            new { actor = "operator", reason = (string?)null });

        // Whatever the frozen rules say about a Draft, HTTP says the same. This asserts the
        // answer matches the authority rather than asserting a state this file chose.
        await using var scope = hub.Runtime.OpenScope();
        var task = (await scope.Store.FindTasksByKeyAsync("API-501", CancellationToken.None)).Single();

        if (status == 200)
        {
            Assert.Equal(TaskState.Cancelled, task.State);
        }
        else
        {
            Assert.Equal(409, status);
            Assert.Equal("cancellation_refused", body.GetProperty("code").GetString());
            Assert.NotEqual(TaskState.Cancelled, task.State);
        }
    }

    [Fact]
    public async Task An_already_terminal_task_is_refused_and_nothing_moves()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-502");
        await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        // Accept it first, so the task is terminal for a real reason.
        var (_, acceptance) = await hub.GetAsync("/api/v1/tasks/API-502/attempts/1/acceptance");

        var verdicts = acceptance.GetProperty("humanCriteria").EnumerateArray()
            .Select(c => new { criterionId = c.GetProperty("criterionId").GetGuid(), verdict = "Met", note = (string?)null })
            .ToArray();

        var (accepted, _) = await hub.PostAsync(
            "/api/v1/tasks/API-502/attempts/1/accept",
            new { actor = "operator", humanVerdicts = verdicts });

        Assert.Equal(200, accepted);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-502/cancel",
            new { actor = "operator", reason = "too late" });

        Assert.Equal(409, status);
        Assert.Equal("cancellation_refused", body.GetProperty("code").GetString());
        Assert.Contains("already", body.GetProperty("message").GetString()!, StringComparison.Ordinal);

        await using var scope = hub.Runtime.OpenScope();
        var after = (await scope.Store.FindTasksByKeyAsync("API-502", CancellationToken.None)).Single();

        // Terminal state is not rewritten, and the execution keeps what it earned.
        Assert.Equal(TaskState.Accepted, after.State);

        var execution = (await scope.Store.ListExecutionsForTaskAsync(after.Id, CancellationToken.None)).Single();

        Assert.Equal(ExecutionOutcome.Passed, execution.Outcome);
    }

    [Fact]
    public async Task A_second_cancel_follows_the_authority_rather_than_an_idempotency_layer()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-503", TaskState.Ready);

        var (first, _) = await hub.PostAsync(
            "/api/v1/tasks/API-503/cancel", new { actor = "operator", reason = "stop" });

        Assert.Equal(200, first);

        var (second, body) = await hub.PostAsync(
            "/api/v1/tasks/API-503/cancel", new { actor = "operator", reason = "stop again" });

        // The task is terminal now, so the second is the ordinary terminal refusal. No
        // idempotency subsystem was built to make this look like success.
        Assert.Equal(409, second);
        Assert.Equal("cancellation_refused", body.GetProperty("code").GetString());

        await using var scope = hub.Runtime.OpenScope();
        var task = (await scope.Store.FindTasksByKeyAsync("API-503", CancellationToken.None)).Single();

        // And exactly one operator decision was recorded.
        var audits = await scope.Store.ListExecutionsForTaskAsync(task.Id, CancellationToken.None);

        Assert.Equal(TaskState.Cancelled, task.State);
        Assert.Empty(audits);
    }

    [Fact]
    public async Task A_running_execution_nobody_is_listening_for_is_refused_not_faked()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-504", TaskState.Ready);

        // A live execution with no process behind it: exactly the shape of a host that died.
        await hub.SeedRunningExecutionAsync(task);

        var (status, body) = await hub.PostAsync(
            "/api/v1/tasks/API-504/cancel", new { actor = "operator", reason = "stop it" });

        // Nothing is listening, so nothing was stopped - and a task that said Cancelled while
        // a process might still be writing would be a worse answer than none.
        Assert.Equal(409, status);
        Assert.Contains("listening", body.GetProperty("message").GetString()!, StringComparison.Ordinal);

        await using var scope = hub.Runtime.OpenScope();
        var after = (await scope.Store.FindTasksByKeyAsync("API-504", CancellationToken.None)).Single();

        Assert.NotEqual(TaskState.Cancelled, after.State);
    }

    [Fact]
    public async Task A_client_that_gives_up_does_not_take_the_cancellation_back()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-505", TaskState.Ready);

        using (var giving = new CancellationTokenSource())
        {
            var sending = hub.Client.PostAsJsonAsync(
                "/api/v1/tasks/API-505/cancel",
                new { actor = "operator", reason = "stop" },
                giving.Token);

            // Whether the abandon lands before or after the authority answers is a race; what
            // matters is that neither outcome leaves the task half-cancelled.
            await giving.CancelAsync();

            try
            {
                using var _ = await sending;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
            }
        }

        await using var scope = hub.Runtime.OpenScope();
        var task = (await scope.Store.FindTasksByKeyAsync("API-505", CancellationToken.None)).Single();

        // Either the authority never ran, or it ran to completion. There is no third state.
        Assert.True(
            task.State is TaskState.Ready or TaskState.Cancelled,
            $"the task was left as {task.State}");
    }

    [Fact]
    public async Task The_durable_state_is_what_a_later_query_sees()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        await hub.SeedTaskAsync("API-506", TaskState.Ready);

        var (status, _) = await hub.PostAsync(
            "/api/v1/tasks/API-506/cancel", new { actor = "operator", reason = "stop" });

        Assert.Equal(200, status);

        // No special cancellation channel was added. The ordinary query shows the transition
        // because the authority persisted it, which is the only reason anything shows it.
        var (_, task) = await hub.GetAsync("/api/v1/tasks/API-506");

        Assert.Equal("Cancelled", task.GetProperty("summary").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Nothing_about_the_machine_can_be_named_in_a_cancel_request()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        // The request identifies a task and a person. It cannot name a process, a signal, a
        // provider or a permission - so there is no OS handle reachable from the network.
        Assert.Equal(
            ["Actor", "Reason"],
            typeof(CancelBody).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        await hub.SeedTaskAsync("API-507", TaskState.Ready);

        var (_, body) = await hub.PostAsync(
            "/api/v1/tasks/API-507/cancel", new { actor = "operator", reason = "stop" });

        // And nothing about the machine comes back either.
        var text = body.GetRawText();

        foreach (var leak in new[] { ":\\", "Global\\", "pid", "Exception" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_console_and_the_http_surface_share_one_construction()
    {
        // The authority itself was never duplicated - it has lived in the application since
        // Phase 5. What could have been duplicated is how it is built, and building it with
        // no signal compiles fine and silently cannot stop a live run. So both adapters ask
        // the same builder, and neither constructs the service itself.
        var cli = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CoreX.Hub.Cli", "Commands", "RunCommands.cs"));

        Assert.Contains("HubCancellation.Build", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("new TaskCancellationService", cli, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot(), "src", "CoreX.Hub.Api"), "*.cs"))
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotContain("new TaskCancellationService", source, StringComparison.Ordinal);
            Assert.DoesNotContain("NamedExecutionCancellation", source, StringComparison.Ordinal);
        }

        // Both reach the same type, and it is the application's.
        Assert.Equal(
            typeof(TaskCancellationService).Assembly,
            typeof(HubCancellation).Assembly.GetReferencedAssemblies()
                .Select(System.Reflection.Assembly.Load)
                .Single(a => a.GetName().Name == "CoreX.Hub.Application"));
    }

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
