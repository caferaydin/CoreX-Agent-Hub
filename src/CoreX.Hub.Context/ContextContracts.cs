using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Context;

/// <summary>
/// One markdown rule document from the trusted context repository.
///
/// Rules never come from the repository being modified: a CLAUDE.md or AGENTS.md found
/// there is untrusted input, and the only way a repository rule reaches a pack is by
/// being taken into the context repository as a recorded decision (architecture H.4).
/// </summary>
public sealed record ContextRuleDocument
{
    public required string Title { get; init; }

    public required string Markdown { get; init; }
}

/// <summary>A recorded decision document, explicitly selected by the caller.</summary>
public sealed record ContextDecisionDocument
{
    /// <summary>File stem inside <c>decisions/</c>. Deterministic, no timestamps.</summary>
    public required string Slug { get; init; }

    public required string Title { get; init; }

    public required string Markdown { get; init; }
}

/// <summary>Live git facts the agent is allowed to know. No credentials, no environment.</summary>
public sealed record ContextRepositoryState
{
    public required Guid RepositoryId { get; init; }

    public required string BaseCommitSha { get; init; }

    public required string BranchName { get; init; }

    public required string DefaultBranch { get; init; }

    public IReadOnlyList<string> ProtectedBranches { get; init; } = [];

    /// <summary>Where the agent may work, relative to the workspace it was given.</summary>
    public string WorkspaceRelativePath { get; init; } = ".";
}

/// <summary>
/// Everything the builder needs. The caller has already decided what is true and what is
/// trusted; the builder serialises that truth deterministically and does not evaluate it.
/// </summary>
public sealed record ContextBuildRequest
{
    public required Guid ExecutionId { get; init; }

    public required string ContextRoot { get; init; }

    public required string TaskKey { get; init; }

    public required string Objective { get; init; }

    public IReadOnlyList<string> AllowedScope { get; init; } = [];

    public IReadOnlyList<string> ForbiddenScope { get; init; } = [];

    /// <summary>Already accepted acceptance truth, serialised as declared.</summary>
    public IReadOnlyList<ContextAcceptanceCriterion> AcceptanceCriteria { get; init; } = [];

    public int AttemptNo { get; init; } = 1;

    public ContextRuleDocument? OrganizationRules { get; init; }

    public ContextRuleDocument? ProjectRules { get; init; }

    public ContextRuleDocument? RepositoryRules { get; init; }

    public IReadOnlyList<ContextDecisionDocument> Decisions { get; init; } = [];

    public required ContextRepositoryState RepositoryState { get; init; }
}

/// <summary>
/// One acceptance criterion as the pack presents it. The Machine and Human distinction
/// stays explicit; nothing here evaluates or rewrites it.
/// </summary>
public sealed record ContextAcceptanceCriterion
{
    public required int Order { get; init; }

    public required string Text { get; init; }

    public VerificationKind? Kind { get; init; }

    public string? VerificationRef { get; init; }

    public string? HumanCheckHint { get; init; }
}

/// <summary>Why a pack could not be produced. There is no partial success.</summary>
public enum ContextBuildFailure
{
    InvalidRequest,
    PathRejected,
    BudgetExceeded,
    PackAlreadyExists,
    MaterializationFailed,
}

public sealed record ContextPackFile(string RelativePath, long SizeBytes, string Sha256);

public sealed record ContextBuildResult
{
    public ContextPack? Pack { get; init; }

    public ContextBuildFailure? Failure { get; init; }

    public string? Detail { get; init; }

    public bool Succeeded => Pack is not null && Failure is null;

    public static ContextBuildResult Ok(ContextPack pack) => new() { Pack = pack };

    public static ContextBuildResult Failed(ContextBuildFailure failure, string detail) =>
        new() { Failure = failure, Detail = detail };

    public string Describe() => Succeeded ? Pack!.Describe() : $"{Failure}: {Detail}";
}

/// <summary>A published context pack. Every field is derived from semantic content only.</summary>
public sealed record ContextPack
{
    public required Guid ExecutionId { get; init; }

    public required string PackPath { get; init; }

    public required string ContextPackHash { get; init; }

    public required IReadOnlyList<ContextPackFile> Files { get; init; }

    public required long TotalBytes { get; init; }

    public string Describe() =>
        $"context pack {ContextPackHash[..12]} ({Files.Count} files, {TotalBytes} bytes)";
}
