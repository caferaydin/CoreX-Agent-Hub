namespace CoreX.Hub.Domain.Tasks;

/// <summary>Outcome of evaluating the ready gate. Immutable and side-effect free.</summary>
public sealed record ReadyGateResult(
    IReadOnlyList<ReadyGateViolation> Violations,
    IReadOnlyList<ReadyGateWarning> Warnings)
{
    public static ReadyGateResult Satisfied { get; } = new([], []);

    public bool IsSatisfied => Violations.Count == 0;

    public string Describe() =>
        Violations.Count == 0
            ? "satisfied"
            : string.Join("; ", Violations.Select(v => v.ToString()));
}
