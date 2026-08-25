using System.Globalization;
using CoreX.Hub.Composition;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Api;

/// <summary>
/// One attempt, from every angle the future UI needs: what it is, what it did step by step,
/// what the machine checks said, what the reviewer found, and what evidence exists.
///
/// All of it is read back from what was persisted while the execution ran. Nothing here
/// re-runs a verification, re-reads a repository or asks git anything - a page that showed
/// a freshly computed answer would be showing something the acceptance decision was never
/// based on.
/// </summary>
public static class ExecutionEndpoints
{
    /// <summary>Raw evidence is streamed only up to this size. Past it, the metadata stands alone.</summary>
    private const long MaximumInlineEvidenceBytes = 8L * 1024 * 1024;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/executions/{id:guid}", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            var execution = await scope.Store.FindExecutionAsync(id, ct);

            if (execution is null)
            {
                return ApiErrors.NotFound("execution");
            }

            var task = await scope.Store.FindTaskAsync(execution.TaskId, ct);

            if (task is null)
            {
                return ApiErrors.NotFound("the execution's task");
            }

            return Results.Ok(Dtos.ExecutionDetail(
                execution,
                task,
                await scope.Store.ListExecutionStepsAsync(id, ct),
                await scope.Store.ListEvidenceAsync(id, ct)));
        });

        api.MapGet("/executions/{id:guid}/steps", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            return Results.Ok(Dtos.Steps(
                await scope.Store.ListExecutionStepsAsync(id, ct),
                await scope.Store.ListEvidenceAsync(id, ct)));
        });

        api.MapGet("/executions/{id:guid}/verification", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            return Results.Ok((await scope.Store.ListVerificationCommandsAsync(id, ct))
                .OrderBy(c => c.Order)
                .Select(Dtos.VerificationCommand));
        });

        api.MapGet("/executions/{id:guid}/review", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            // Canonical Hub findings, with the Hub's own scope flag and the Hub's own gate.
            // What the provider claimed about scope never reaches this response.
            return Results.Ok(Dtos.Review(await scope.Store.ListFindingsAsync(id, ct)));
        });

        api.MapGet("/executions/{id:guid}/evidence", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            return Results.Ok((await scope.Store.ListEvidenceAsync(id, ct))
                .OrderBy(a => a.Kind.ToString(), StringComparer.Ordinal)
                .Select(Dtos.Evidence));
        });

        // The result diff, from the durable artifact rather than from git. Its own route
        // because it is the one piece of evidence the UI always wants.
        api.MapGet("/executions/{id:guid}/diff", async (Guid id, HubRuntime runtime, CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            var diff = (await scope.Store.ListEvidenceAsync(id, ct))
                .FirstOrDefault(a => a.Kind == EvidenceKind.GitCommitDiff);

            if (diff is null)
            {
                return ApiErrors.NotFound("a result diff for this execution");
            }

            return await StreamAsync(runtime, diff, "text/plain; charset=utf-8", ct);
        });

        // Raw evidence by identity only. There is deliberately no path parameter anywhere in
        // this file: an artifact is addressed by the execution that owns it and its own id,
        // both of which the database has to agree with.
        api.MapGet("/executions/{id:guid}/evidence/{evidenceId:guid}/content", async (
            Guid id,
            Guid evidenceId,
            HubRuntime runtime,
            CancellationToken ct) =>
        {
            await using var scope = runtime.OpenScope();

            if (await scope.Store.FindExecutionAsync(id, ct) is null)
            {
                return ApiErrors.NotFound("execution");
            }

            var artifact = (await scope.Store.ListEvidenceAsync(id, ct))
                .FirstOrDefault(a => a.Id == evidenceId);

            // Scoped, not just found: an artifact id that belongs to another execution is a
            // 404 here, so a guessed id cannot read across executions.
            if (artifact is null)
            {
                return ApiErrors.NotFound("evidence for this execution");
            }

            return await StreamAsync(runtime, artifact, artifact.MediaType, ct);
        });

        api.MapGet("/executions/{id:guid}/events", async (
            Guid id,
            HttpContext http,
            ExecutionEventStream events,
            CancellationToken ct) =>
        {
            var after = ResumeFrom(http.Request);

            // The client's token, and only the client's token. Nothing an execution owns is
            // reachable from here.
            await events.WriteAsync(http.Response, id, after, http.RequestAborted);
        });
    }

    /// <summary>
    /// Where a reconnecting client left off.
    ///
    /// The browser resends its last event id automatically; a query parameter is accepted
    /// too, because a client that reconnects deliberately should not have to fake a header.
    /// Anything unparseable means "start from the beginning", which is safe: the snapshot
    /// carries current truth either way.
    /// </summary>
    private static long ResumeFrom(HttpRequest request)
    {
        var header = request.Headers["Last-Event-ID"].ToString();

        if (long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromHeader))
        {
            return Math.Max(0, fromHeader);
        }

        return long.TryParse(request.Query["after"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)
            ? Math.Max(0, q)
            : 0;
    }

    /// <summary>
    /// Hands back a stored blob through the evidence store, never through a caller-supplied
    /// path. The size ceiling is what stops a browser tab from being handed a gigabyte.
    /// </summary>
    private static async Task<IResult> StreamAsync(
        HubRuntime runtime,
        Domain.Executions.EvidenceArtifact artifact,
        string contentType,
        CancellationToken ct)
    {
        if (artifact.SizeBytes > MaximumInlineEvidenceBytes)
        {
            return ApiErrors.Refused(
                "evidence_too_large",
                $"this artifact is {artifact.SizeBytes} bytes, which is beyond what this endpoint streams",
                $"limit {MaximumInlineEvidenceBytes} bytes");
        }

        var blobs = CoreX.Hub.Composition.Verification.VerificationComposition
            .CreateEvidenceStore(runtime.DataDirectory);

        string? content;

        try
        {
            content = await blobs.ReadTextAsync(artifact.Sha256, ct);
        }
        catch (IOException)
        {
            // The row says the blob exists and the store disagrees. Worth saying plainly
            // rather than turning into a 500 that suggests the Hub is broken.
            content = null;
        }

        return content is null
            ? ApiErrors.NotFound("the stored content for this artifact")
            : Results.Text(content, contentType);
    }
}
