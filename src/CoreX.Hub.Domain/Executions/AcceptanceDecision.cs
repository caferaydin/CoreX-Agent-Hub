using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Executions;

/// <summary>One human verdict on one Human acceptance criterion (ADR-0015).</summary>
public sealed record HumanCriterionVerdictRecord(
    Guid CriterionId,
    HumanCriterionVerdict Verdict,
    string? Note);

/// <summary>
/// Immutable human decision on an execution (ADR-0014).
///
/// There is no separate table for the review override: this record already carries
/// <see cref="OverrideUsed"/>, <see cref="Reason"/>, <see cref="HumanCriterionVerdicts"/>,
/// <see cref="Actor"/> and <see cref="DecidedAtUtc"/>.
///
/// The type has no mutators at all: a decision is written once and never revised.
/// It also never carries an execution outcome, because acceptance must never rewrite it.
///
/// <see cref="Source"/> says whether a person took it or a repository's autopilot did
/// (ADR-0028). A policy decision is the narrowest one there is: a plain Accept, no override,
/// no human verdicts - the policy may only say yes to what the evidence already said.
/// </summary>
public sealed class AcceptanceDecision
{
    private List<HumanCriterionVerdictRecord> _humanCriterionVerdicts = [];

    private AcceptanceDecision()
    {
        // EF Core
    }

    public AcceptanceDecision(
        Guid id,
        Guid organizationId,
        Guid executionId,
        AcceptanceDecisionKind decision,
        string actor,
        string? reason,
        bool overrideUsed,
        IEnumerable<HumanCriterionVerdictRecord> humanCriterionVerdicts,
        DateTimeOffset decidedAtUtc,
        AcceptanceDecisionSource source = AcceptanceDecisionSource.Person)
    {
        Id = Guard.NotEmpty(id, nameof(id));
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        ExecutionId = Guard.NotEmpty(executionId, nameof(executionId));
        Decision = decision;
        Actor = Guard.NotBlankMax(actor, nameof(actor), 200);
        Reason = Guard.OptionalMax(reason, nameof(reason), 2000);
        OverrideUsed = overrideUsed;
        _humanCriterionVerdicts = [.. humanCriterionVerdicts ?? []];
        DecidedAtUtc = decidedAtUtc;
        Source = source;

        if (OverrideUsed && Reason is null)
        {
            // ADR-0014: an override may never be silent.
            throw new DomainException("A review override acceptance requires a non-empty reason.");
        }

        if (Source == AcceptanceDecisionSource.Policy)
        {
            // ADR-0028: a policy only ever says yes to what the evidence already said.
            if (Decision != AcceptanceDecisionKind.Accept)
            {
                throw new DomainException($"A policy may only accept; {Decision} is a person's decision.");
            }

            if (OverrideUsed)
            {
                throw new DomainException("A policy never overrides a review; that is a person's decision.");
            }

            if (_humanCriterionVerdicts.Count > 0)
            {
                throw new DomainException("A policy has no human verdict to give.");
            }
        }
        else if (Source != AcceptanceDecisionSource.Person)
        {
            throw new DomainException($"Unknown decision source {(int)Source}.");
        }

        var duplicate = _humanCriterionVerdicts
            .GroupBy(v => v.CriterionId)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new DomainException($"Duplicate human verdict for criterion {duplicate.Key}.");
        }
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ExecutionId { get; private set; }

    public AcceptanceDecisionKind Decision { get; private set; }

    public string Actor { get; private set; } = null!;

    public string? Reason { get; private set; }

    /// <summary>True only for the ReviewBlocked acceptance override (ADR-0014).</summary>
    public bool OverrideUsed { get; private set; }

    public IReadOnlyList<HumanCriterionVerdictRecord> HumanCriterionVerdicts => _humanCriterionVerdicts;

    public DateTimeOffset DecidedAtUtc { get; private set; }

    /// <summary>A person, or a repository's autopilot (ADR-0028).</summary>
    public AcceptanceDecisionSource Source { get; private set; }
}
