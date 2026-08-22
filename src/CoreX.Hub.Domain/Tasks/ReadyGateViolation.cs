namespace CoreX.Hub.Domain.Tasks;

/// <summary>Why a task may not become <see cref="TaskState.Ready"/>.</summary>
public enum ReadyGateViolationCode
{
    TaskNotInDraft,
    OrganizationNotFound,
    OrganizationBlocked,
    ProjectNotFound,
    ProjectOrganizationMismatch,
    RepositoryNotFound,
    RepositoryProjectMismatch,
    ObjectiveMissing,
    AllowedScopeMissing,
    ScopeEntryConflict,
    VerificationProfileMissing,
    VerificationProfileRepositoryMismatch,
    NoAcceptanceCriteria,
    CriterionVerificationKindMissing,
    MachineCriterionMissingVerificationRef,
    MachineCriterionVerificationRefUnresolved,
}

/// <summary>Advisory signals that do not block readiness (architecture B.1).</summary>
public enum ReadyGateWarningCode
{
    NoMachineCriterion,
}

public sealed record ReadyGateViolation(ReadyGateViolationCode Code, string Message, string? Subject = null)
{
    public override string ToString() =>
        Subject is null ? $"{Code}: {Message}" : $"{Code} [{Subject}]: {Message}";
}

public sealed record ReadyGateWarning(ReadyGateWarningCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}
