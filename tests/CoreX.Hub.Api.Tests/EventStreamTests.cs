using System.Runtime.Versioning;
using System.Text.Json;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// The event stream, and the properties that make it safe to have one.
///
/// The stream is a convenience. The database is the truth. Everything asserted here is a
/// version of that sentence: a client that reconnects catches up, a client that leaves
/// changes nothing, and a client that stops reading cannot reach the work.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventStreamTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_stream_opens_with_the_current_truth_before_any_event()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-201");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        var frames = await ReadAsync(hub, execution.Id, until: f => f.Type == "complete");

        var snapshot = frames.First(f => f.Type == "snapshot");

        Assert.Equal("Completed", snapshot.Data.GetProperty("state").GetString());
        Assert.Equal("Passed", snapshot.Data.GetProperty("outcome").GetString());
        Assert.True(snapshot.Data.GetProperty("terminal").GetBoolean());

        // A UI can render the whole page from this one frame; the events that follow are
        // updates, not the only way to learn what happened.
        Assert.Equal(JsonValueKind.Array, snapshot.Data.GetProperty("steps").ValueKind);
        Assert.Equal("AwaitingAcceptance", snapshot.Data.GetProperty("taskState").GetString());
    }

    [Fact]
    public async Task Durable_events_reach_the_stream_in_order_and_carry_a_cursor()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-202");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed, decorate: (scope, e) =>
        {
            foreach (var (type, message, at) in new[]
                     {
                         ("Verifying", "entered Verifying", 1),
                         ("Reviewing", "entered Reviewing", 2),
                         ("Completed", "Passed: 0 finding(s)", 3),
                     })
            {
                scope.Store.AddOrchestrationEvent(new OrchestrationEvent(
                    Guid.CreateVersion7(), e.OrganizationId, e.Id, type, message,
                    DateTimeOffset.UtcNow.AddSeconds(at)));
            }
        });

        var frames = await ReadAsync(hub, execution.Id, until: f => f.Type == "complete");
        var events = frames.Where(f => f.Type == "execution-event").ToList();

        Assert.Equal(3, events.Count);
        Assert.Equal(["Verifying", "Reviewing", "Completed"], events.Select(e => e.Data.GetProperty("type").GetString()));

        // The cursor is an ordinal over the durable timeline, and it is what a reconnect
        // resumes from.
        Assert.Equal([1L, 2L, 3L], events.Select(e => e.Data.GetProperty("sequence").GetInt64()));
        Assert.Equal(["1", "2", "3"], events.Select(e => e.Id));

        // UTC on the wire, like every other timestamp.
        Assert.All(events, e => Assert.EndsWith(
            "Z", e.Data.GetProperty("occurredAtUtc").GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reconnecting_client_gets_the_rest_and_not_the_beginning_again()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-203");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed, decorate: (scope, e) =>
        {
            for (var i = 1; i <= 4; i++)
            {
                scope.Store.AddOrchestrationEvent(new OrchestrationEvent(
                    Guid.CreateVersion7(), e.OrganizationId, e.Id, $"Step{i}", $"message {i}",
                    DateTimeOffset.UtcNow.AddSeconds(i)));
            }
        });

        // What the browser sends by itself after a dropped connection.
        var resumed = await ReadAsync(
            hub, execution.Id, until: f => f.Type == "complete", lastEventId: "2");

        var events = resumed.Where(f => f.Type == "execution-event").ToList();

        Assert.Equal(["Step3", "Step4"], events.Select(e => e.Data.GetProperty("type").GetString()));

        // And the snapshot still comes first, so nothing has to be inferred from the gap.
        Assert.Equal("snapshot", resumed[0].Type);
    }

    [Fact]
    public async Task A_client_that_disconnects_changes_nothing_about_the_execution()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-204");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        // Opened and abandoned mid-stream, which is what a closed tab looks like.
        using (var giving = new CancellationTokenSource())
        {
            using var response = await hub.Client.GetAsync(
                $"/api/v1/executions/{execution.Id:D}/events",
                HttpCompletionOption.ResponseHeadersRead,
                giving.Token);

            await giving.CancelAsync();
        }

        await using var scope = hub.Runtime.OpenScope();
        var after = await scope.Store.FindExecutionAsync(execution.Id, CancellationToken.None);

        // Untouched. Watching is not owning.
        Assert.NotNull(after);
        Assert.Equal(ExecutionState.Completed, after!.State);
        Assert.Equal(ExecutionOutcome.Passed, after.Outcome);

        var owning = await scope.Store.FindTaskAsync(task.Id, CancellationToken.None);

        Assert.Equal(Domain.Tasks.TaskState.AwaitingAcceptance, owning!.State);
    }

    [Fact]
    public async Task A_consumer_that_stops_reading_cannot_reach_the_work()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var task = await hub.SeedTaskAsync("API-205");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        // Headers only, then nothing: the body is never drained.
        using var slow = await hub.Client.GetAsync(
            $"/api/v1/executions/{execution.Id:D}/events",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", slow.Content.Headers.ContentType!.MediaType);

        // While that sits there, everything else still answers immediately. The stream reads
        // durable state on its own schedule and holds nothing the rest of the Hub needs, so a
        // dead consumer costs one socket and nothing more.
        var answered = System.Diagnostics.Stopwatch.StartNew();

        var (status, _) = await hub.GetAsync($"/api/v1/executions/{execution.Id:D}");

        answered.Stop();

        Assert.Equal(200, status);
        Assert.True(answered.Elapsed < TimeSpan.FromSeconds(10), $"took {answered.Elapsed}");
    }

    [Fact]
    public async Task A_stream_for_an_execution_that_does_not_exist_says_so_and_ends()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var frames = await ReadAsync(hub, Guid.CreateVersion7(), until: f => f.Type == "error");

        var error = Assert.Single(frames);

        Assert.Equal("error", error.Type);
        Assert.Contains("not found", error.Data.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // --- Reading the wire format --------------------------------------------------------

    private sealed record Frame(string Type, string? Id, JsonElement Data);

    private static async Task<IReadOnlyList<Frame>> ReadAsync(
        HubApiFixture hub,
        Guid executionId,
        Func<Frame, bool> until,
        string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/executions/{executionId:D}/events");

        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        using var giving = new CancellationTokenSource(Patience);
        using var response = await hub.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, giving.Token);

        await using var body = await response.Content.ReadAsStreamAsync(giving.Token);
        using var reader = new StreamReader(body);

        var frames = new List<Frame>();
        string? id = null;
        string? type = null;

        while (await reader.ReadLineAsync(giving.Token) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line[4..];
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var frame = new Frame(type!, id, JsonDocument.Parse(line[6..]).RootElement.Clone());

                frames.Add(frame);
                id = null;

                if (until(frame))
                {
                    return frames;
                }
            }
        }

        return frames;
    }
}
