namespace CoreX.Hub.Application.Gates;

/// <summary>
/// What the three gates decided together.
///
/// The result commit becomes possible only when all three passed - and K0 as well, which
/// the orchestrator will add. This type states eligibility; it does not act on it.
/// </summary>
public sealed record GateEvaluationResult
{
    public required IReadOnlyList<GateVerdict> Verdicts { get; init; }

    /// <summary>The first gate that blocked, in K1, K2, K3 order. Null when all passed.</summary>
    public GateVerdict? BlockingVerdict => Verdicts.FirstOrDefault(v => v.Blocked);

    /// <summary>
    /// True only when every gate passed. Phase 5C additionally requires K0 before a result
    /// commit is actually attempted; nothing here creates one.
    /// </summary>
    public bool EligibleForResultCommit => BlockingVerdict is null;

    public GateVerdict Verdict(GateKind kind) => Verdicts.Single(v => v.Kind == kind);

    public string Describe() =>
        BlockingVerdict?.Describe() ?? "all gates passed";
}

/// <summary>
/// Runs K1, K2 and K3 over already captured machine facts and reports what they decided.
///
/// Deliberately not an orchestrator, and deliberately not named like one. It changes no
/// execution state, no task state, writes no acceptance decision, runs no verification,
/// creates no commit and releases no worktree. Sequencing the wider lifecycle is Phase 5C.
///
/// Evidence comes first and gate precedence comes second. Every gate is evaluated so the
/// diagnostics are complete - an out of scope write and a secret in the same run are both
/// worth knowing about - but the outcome is decided by the first gate that blocked, in
/// K1, K2, K3 order. Collecting a diagnostic is not the same as a gate winning.
/// </summary>
public sealed class ExecutionSafetyGates
{
    private readonly SecretGuard _secretGuard;

    public ExecutionSafetyGates(SecretGuard secretGuard) =>
        _secretGuard = secretGuard ?? throw new ArgumentNullException(nameof(secretGuard));

    public GateEvaluationResult Evaluate(
        CapturedChanges changes,
        IReadOnlyList<string> allowedScope,
        IReadOnlyList<string> forbiddenScope)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var verdicts = new List<GateVerdict>
        {
            RuntimePolicyGate.Evaluate(changes),
            ScopeGuard.Evaluate(changes, allowedScope, forbiddenScope),
            _secretGuard.Evaluate(changes),
        };

        return new GateEvaluationResult { Verdicts = verdicts };
    }
}
