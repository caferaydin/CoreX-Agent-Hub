using System.Globalization;
using System.Text.Json;
using CoreX.Hub.Composition;

namespace CoreX.Hub.Api;

/// <summary>
/// One event, in terms a UI can render without knowing which provider ran.
///
/// <see cref="Sequence"/> is the position of this event in the execution's durable timeline,
/// counting from one. It is the reconnect cursor, and it is an ordinal rather than a database
/// key on purpose: the timeline is append-only and its order is stable, so "everything after
/// the twelfth" is exact without a new column to store it in.
/// </summary>
public sealed record ExecutionEventDto
{
    public required long Sequence { get; init; }

    public required Guid ExecutionId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required string Type { get; init; }

    public required string Message { get; init; }
}

/// <summary>The current truth, sent first so a client is never rendering from events alone.</summary>
public sealed record ExecutionSnapshotDto
{
    public required Guid ExecutionId { get; init; }

    public required string State { get; init; }

    public string? Outcome { get; init; }

    public required string TaskState { get; init; }

    public required bool Terminal { get; init; }

    public required long Sequence { get; init; }

    public IReadOnlyList<ExecutionStepDto> Steps { get; init; } = [];
}

/// <summary>
/// Server-sent events over the durable timeline.
///
/// This is a projection, not a bus. It reads the same rows a page reload would read, which
/// is what makes the two agree by construction: there is no in-memory queue that could hold
/// an event the database never got, and none that could lose one the database has.
///
/// It also means a slow consumer cannot reach the orchestrator. Nothing subscribes to the
/// running execution; the stream polls durable state at a bounded interval and writes what it
/// finds. A browser that stops reading fills a socket buffer and eventually fails a write,
/// and the only thing that ends is this loop.
/// </summary>
public sealed class ExecutionEventStream
{
    /// <summary>How often durable state is re-read while an execution is live.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How long a single write may take before the consumer is treated as gone.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to keep streaming after an execution reaches a terminal state.</summary>
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The same wire contract the rest of the API uses.
    ///
    /// Stated here rather than inherited, because a stream that serialised its own way would
    /// hand a client two shapes for one fact - which is exactly what happened before this
    /// line existed: REST timestamps ended in Z and stream timestamps carried a local offset.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new UtcInstantConverter(),
        },
    };

    private readonly HubRuntime _runtime;

    public ExecutionEventStream(HubRuntime runtime) => _runtime = runtime;

    /// <summary>
    /// Streams until the execution is terminal or the client goes away.
    ///
    /// The client's token is the only thing that stops this, and it stops nothing else. An
    /// execution is not owned by whoever happens to be watching it.
    /// </summary>
    public async Task WriteAsync(HttpResponse response, Guid executionId, long after, CancellationToken clientGone)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var sent = after;
        var terminalSince = (DateTimeOffset?)null;

        while (!clientGone.IsCancellationRequested)
        {
            ExecutionSnapshotDto snapshot;
            IReadOnlyList<ExecutionEventDto> fresh;

            await using (var scope = _runtime.OpenScope())
            {
                var execution = await scope.Store.FindExecutionAsync(executionId, clientGone);

                if (execution is null)
                {
                    await SendAsync(response, "error", new { message = "execution not found" }, null, clientGone);
                    return;
                }

                var task = await scope.Store.FindTaskAsync(execution.TaskId, clientGone);
                var steps = await scope.Store.ListExecutionStepsAsync(executionId, clientGone);
                var evidence = await scope.Store.ListEvidenceAsync(executionId, clientGone);
                var timeline = await scope.Store.ListOrchestrationEventsAsync(executionId, clientGone);

                snapshot = new ExecutionSnapshotDto
                {
                    ExecutionId = executionId,
                    State = execution.State.ToString(),
                    Outcome = execution.Outcome?.ToString(),
                    TaskState = task?.State.ToString() ?? "Unknown",
                    Terminal = Dtos.IsTerminal(execution.State),
                    Sequence = timeline.Count,
                    Steps = Dtos.Steps(steps, evidence),
                };

                fresh =
                [
                    .. timeline
                        .Select((e, i) => new ExecutionEventDto
                        {
                            Sequence = i + 1,
                            ExecutionId = executionId,
                            OccurredAtUtc = e.OccurredAtUtc.ToUniversalTime(),
                            Type = e.EventType,
                            Message = e.Message,
                        })
                        .Where(e => e.Sequence > sent),
                ];
            }

            // The snapshot goes first on every pass, so a client that joined late, missed a
            // window, or reconnected without a cursor is never left reconstructing state from
            // a partial event stream.
            if (sent == after)
            {
                await SendAsync(response, "snapshot", snapshot, null, clientGone);
            }

            foreach (var e in fresh)
            {
                await SendAsync(response, "execution-event", e, e.Sequence, clientGone);
                sent = e.Sequence;
            }

            if (snapshot.Terminal)
            {
                // One last pass after the terminal state, so the closing events of the run are
                // not cut off by the state that produced them.
                terminalSince ??= DateTimeOffset.UtcNow;

                if (DateTimeOffset.UtcNow - terminalSince > TerminalLinger)
                {
                    await SendAsync(response, "complete", snapshot, null, clientGone);
                    return;
                }
            }

            try
            {
                await Task.Delay(Interval, clientGone);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task SendAsync(
        HttpResponse response,
        string type,
        object payload,
        long? id,
        CancellationToken clientGone)
    {
        var text = new System.Text.StringBuilder();

        if (id is { } sequence)
        {
            text.Append("id: ").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append("event: ").Append(type).Append('\n')
            .Append("data: ").Append(JsonSerializer.Serialize(payload, Json)).Append("\n\n");

        // Bounded on purpose. A consumer that has stopped reading must cost this loop a
        // timeout and nothing else - never a blocked write held open while an agent runs.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(clientGone);
        bounded.CancelAfter(WriteTimeout);

        await response.WriteAsync(text.ToString(), bounded.Token);
        await response.Body.FlushAsync(bounded.Token);
    }
}
