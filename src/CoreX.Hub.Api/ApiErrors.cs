using System.Diagnostics;
using CoreX.Hub.Application.Acceptance;
using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Tasks;
using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Api;

/// <summary>
/// One shape for everything that goes wrong, so a client never has to guess.
///
/// The <see cref="CorrelationId"/> is the point of the envelope. An operator sees a short
/// identifier, the log holds the whole story, and nothing in between has to choose between
/// being useful and being safe.
/// </summary>
public sealed record ApiError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>Extra facts that are safe to show. Never a stack trace, never a path.</summary>
    public IReadOnlyList<string>? Details { get; init; }
}

/// <summary>
/// Turns an application failure into a status code and an envelope.
///
/// The mapping is by exception type because the application already draws the distinctions
/// that matter: a refused decision is not a missing task, and neither is a bug. What this
/// must never do is invent a new distinction - it maps, and the reason text comes from the
/// authority that refused.
/// </summary>
public static class ApiErrors
{
    public static IResult Refused(string code, string message, params string[] details) =>
        Results.Json(
            Envelope(code, message, details),
            statusCode: StatusCodes.Status409Conflict);

    public static IResult NotFound(string what) =>
        Results.Json(
            Envelope("not_found", what + " was not found"),
            statusCode: StatusCodes.Status404NotFound);

    public static IResult Invalid(string message, params string[] details) =>
        Results.Json(
            Envelope("invalid_request", message, details),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Maps a known application failure. Returns null when the exception is not one of
    /// them, which is the caller's signal to let it become a 500 instead of guessing.
    /// </summary>
    public static IResult? FromException(Exception exception) => exception switch
    {
        // A human decision the Hub refused. The reason is the whole value of the response,
        // and it was written for a person to read.
        AcceptanceException e => Refused("acceptance_refused", e.Message),

        // A task operation the catalog would not carry out.
        TaskOperationException e => Refused("task_refused", e.Message),

        // A rule the model itself enforces - a state transition that does not exist.
        DomainException e => Refused("domain_refused", e.Message),

        // The installation is not set up the way this request needs.
        HubConfigurationException e => Refused("configuration_refused", e.Message),

        // An execution that could not be started: a lease held, a slot taken, a repository
        // that is not ready. Conflict rather than error - nothing is broken.
        ExecutionStartException e => Refused("execution_refused", e.Message),

        ArgumentException e => Invalid(e.Message),

        OperationCanceledException => Results.Json(
            Envelope("cancelled", "the request was cancelled"),
            statusCode: StatusCodes.Status499ClientClosedRequest),

        _ => null,
    };

    /// <summary>
    /// The last resort. Says nothing about what went wrong, because at this point the Hub
    /// does not know either, and the exception message may carry a path or an environment
    /// value. The correlation id is how the log entry is found.
    /// </summary>
    public static ApiError Unexpected() => Envelope(
        "internal_error",
        "the Hub could not complete this request; the details are in the local log");

    private static ApiError Envelope(string code, string message, IReadOnlyList<string>? details = null) => new()
    {
        Code = code,
        Message = message,
        CorrelationId = Activity.Current?.Id ?? Guid.CreateVersion7().ToString("N"),
        Details = details is { Count: > 0 } ? details : null,
    };
}

/// <summary>Status codes the framework does not name.</summary>
internal static class StatusCodesExtra
{
    public const int ClientClosedRequest = 499;
}
