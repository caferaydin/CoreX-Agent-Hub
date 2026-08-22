using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Application.Tasks;

/// <summary>Raised when a task operation cannot be carried out against the catalog.</summary>
public sealed class TaskOperationException : Exception
{
    public TaskOperationException(string message) : base(message)
    {
    }
}

/// <summary>One acceptance criterion as supplied by the caller (ADR-0015).</summary>
public sealed record CriterionInput(
    VerificationKind? Kind,
    string Text,
    string? VerificationRef = null,
    string? HumanCheckHint = null);

public sealed record CreateTaskRequest
{
    public required string OrganizationSlug { get; init; }

    public required string ProjectSlug { get; init; }

    /// <summary>Optional when the project owns exactly one repository.</summary>
    public string? RepositorySlug { get; init; }

    public required string Objective { get; init; }

    /// <summary>Optional explicit key; otherwise the next free number for the project prefix.</summary>
    public string? TaskKey { get; init; }

    public IReadOnlyList<string> AllowedScope { get; init; } = [];

    public IReadOnlyList<string> ForbiddenScope { get; init; } = [];

    public IReadOnlyList<CriterionInput> Criteria { get; init; } = [];

    public required string Actor { get; init; }
}

/// <summary>
/// Everything the CLI needs to render a task, including a fresh ready gate evaluation.
/// </summary>
public sealed record TaskView(
    HubTask Task,
    Organization Organization,
    Project Project,
    Repository Repository,
    VerificationProfile? VerificationProfile,
    ReadyGateResult ReadyGate);

public sealed record MarkReadyResult(TaskView View, bool Transitioned);
