using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Domain.Tasks;

/// <summary>
/// Draft -&gt; Ready admission check (architecture B.1 / ADR-0015).
///
/// This is an explicit, hard-coded checklist. It is deliberately NOT a rule DSL or a
/// policy engine (architecture W #1, W #4): adding a condition means adding a line here.
/// </summary>
public static class ReadyGate
{
    public static ReadyGateResult Evaluate(
        HubTask task,
        Organization? organization,
        Project? project,
        Repository? repository,
        VerificationProfile? verificationProfile)
    {
        ArgumentNullException.ThrowIfNull(task);

        var violations = new List<ReadyGateViolation>();
        var warnings = new List<ReadyGateWarning>();

        if (task.State is not (TaskState.Draft or TaskState.Ready))
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.TaskNotInDraft,
                $"Task is in state {task.State}; the ready gate only applies to a draft task."));
        }

        EvaluateOrganization(task, organization, violations);
        EvaluateProject(task, organization, project, violations);
        EvaluateRepository(task, project, repository, violations);
        EvaluateObjectiveAndScope(task, violations);
        var profileUsable = EvaluateVerificationProfile(task, repository, verificationProfile, violations);
        EvaluateCriteria(task, profileUsable ? verificationProfile : null, violations, warnings);

        return new ReadyGateResult(violations, warnings);
    }

    private static void EvaluateOrganization(
        HubTask task,
        Organization? organization,
        List<ReadyGateViolation> violations)
    {
        if (organization is null)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.OrganizationNotFound,
                "Target organization does not exist.",
                task.OrganizationId.ToString()));
            return;
        }

        if (!organization.CanStartExecution)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.OrganizationBlocked,
                organization.BlockReason ?? "Organization is blocked.",
                organization.Slug.Value));
        }
    }

    private static void EvaluateProject(
        HubTask task,
        Organization? organization,
        Project? project,
        List<ReadyGateViolation> violations)
    {
        if (project is null)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.ProjectNotFound,
                "Target project does not exist.",
                task.ProjectId.ToString()));
            return;
        }

        if (organization is not null && project.OrganizationId != organization.Id)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.ProjectOrganizationMismatch,
                "Target project belongs to a different organization.",
                project.Slug.Value));
        }
    }

    private static void EvaluateRepository(
        HubTask task,
        Project? project,
        Repository? repository,
        List<ReadyGateViolation> violations)
    {
        if (repository is null)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.RepositoryNotFound,
                "Target repository does not exist.",
                task.RepositoryId.ToString()));
            return;
        }

        if (project is not null && repository.ProjectId != project.Id)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.RepositoryProjectMismatch,
                "Target repository belongs to a different project.",
                repository.Slug.Value));
        }
    }

    private static void EvaluateObjectiveAndScope(HubTask task, List<ReadyGateViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(task.Objective))
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.ObjectiveMissing,
                "Task objective is missing."));
        }

        if (task.AllowedScope.Count == 0)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.AllowedScopeMissing,
                "Task must declare at least one allowed scope glob."));
        }

        var conflicting = task.AllowedScope
            .Intersect(task.ForbiddenScope, StringComparer.Ordinal)
            .ToList();

        foreach (var glob in conflicting)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.ScopeEntryConflict,
                "Scope glob is declared as both allowed and forbidden.",
                glob));
        }
    }

    private static bool EvaluateVerificationProfile(
        HubTask task,
        Repository? repository,
        VerificationProfile? verificationProfile,
        List<ReadyGateViolation> violations)
    {
        if (verificationProfile is null)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.VerificationProfileMissing,
                "Target repository has no default verification profile.",
                repository?.Slug.Value ?? task.RepositoryId.ToString()));
            return false;
        }

        if (repository is not null && verificationProfile.RepositoryId != repository.Id)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.VerificationProfileRepositoryMismatch,
                "Verification profile belongs to a different repository.",
                verificationProfile.Name));
            return false;
        }

        return true;
    }

    private static void EvaluateCriteria(
        HubTask task,
        VerificationProfile? verificationProfile,
        List<ReadyGateViolation> violations,
        List<ReadyGateWarning> warnings)
    {
        var criteria = task.AcceptanceCriteria;

        if (criteria.Count == 0)
        {
            violations.Add(new ReadyGateViolation(
                ReadyGateViolationCode.NoAcceptanceCriteria,
                "Task must declare at least one acceptance criterion."));
            return;
        }

        foreach (var criterion in criteria)
        {
            var subject = $"#{criterion.Order} {criterion.Text}";

            if (criterion.Kind is null)
            {
                // ADR-0015: the REJECTED rule was "every criterion must be machine verifiable".
                // The ACCEPTED rule is "the verification METHOD must be declared".
                violations.Add(new ReadyGateViolation(
                    ReadyGateViolationCode.CriterionVerificationKindMissing,
                    "Acceptance criterion does not declare a verification kind (Machine or Human).",
                    subject));
                continue;
            }

            if (criterion.Kind != VerificationKind.Machine)
            {
                // Human criteria are valid and require no verification command.
                continue;
            }

            if (string.IsNullOrWhiteSpace(criterion.VerificationRef))
            {
                violations.Add(new ReadyGateViolation(
                    ReadyGateViolationCode.MachineCriterionMissingVerificationRef,
                    "Machine acceptance criterion must declare a verification step reference.",
                    subject));
                continue;
            }

            if (verificationProfile is not null && !verificationProfile.HasStep(criterion.VerificationRef))
            {
                violations.Add(new ReadyGateViolation(
                    ReadyGateViolationCode.MachineCriterionVerificationRefUnresolved,
                    $"Verification reference '{criterion.VerificationRef}' does not resolve to a step in profile '{verificationProfile.Name}'. Known steps: {string.Join(", ", verificationProfile.StepNames)}.",
                    subject));
            }
        }

        if (criteria.All(c => c.Kind != VerificationKind.Machine))
        {
            warnings.Add(new ReadyGateWarning(
                ReadyGateWarningCode.NoMachineCriterion,
                "Task declares no Machine acceptance criterion; there is no automatic signal beyond the repository verification profile."));
        }
    }
}
