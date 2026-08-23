namespace CoreX.Hub.Application.Gates;

/// <summary>The three fixed product gates. Not a registry, not a plugin point.</summary>
public enum GateKind
{
    /// <summary>K1: the agent must not have committed.</summary>
    RuntimePolicy,

    /// <summary>K2: changed paths must stay inside the task's declared scope.</summary>
    Scope,

    /// <summary>K3: added diff lines must contain no secret.</summary>
    Secret,
}

public enum GateStatus
{
    Passed,
    Blocked,
}

/// <summary>One out of scope path and the reason it was refused.</summary>
public sealed record ScopeEscape(string RelativePath, string Reason);

/// <summary>
/// One secret pattern hit. It records where and which family - never the value.
/// A matched secret is not evidence worth keeping; the fact that one exists is.
/// </summary>
public sealed record SecretHit(string PatternName, string RelativePath, int? LineNumber)
{
    public string Describe() =>
        LineNumber is { } line
            ? $"{PatternName} in {RelativePath} line {line}"
            : $"{PatternName} in {RelativePath}";
}

/// <summary>
/// What one gate decided, and the bounded facts behind it.
///
/// A verdict is a statement about machine truth. It changes no execution state, writes no
/// evidence and performs no git operation: the caller decides what to do with it, and in
/// this phase there is no caller that does anything but read it.
/// </summary>
public sealed record GateVerdict
{
    public required GateKind Kind { get; init; }

    public required GateStatus Status { get; init; }

    /// <summary>Short machine readable reason. Empty when the gate passed.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Set by K1 only, and only when HEAD moved.</summary>
    public bool UnexpectedAgentCommitDetected { get; init; }

    /// <summary>Commit SHAs observed between the base commit and HEAD, as captured.</summary>
    public IReadOnlyList<string> UnexpectedCommitShas { get; init; } = [];

    public IReadOnlyList<ScopeEscape> ScopeEscapes { get; init; } = [];

    public IReadOnlyList<SecretHit> SecretHits { get; init; } = [];

    public bool Blocked => Status == GateStatus.Blocked;

    public string Describe() => Blocked ? $"{Kind} blocked: {Reason}" : $"{Kind} passed";

    public static GateVerdict Pass(GateKind kind) => new() { Kind = kind, Status = GateStatus.Passed };

    public static GateVerdict Block(GateKind kind, string reason) =>
        new() { Kind = kind, Status = GateStatus.Blocked, Reason = reason };
}
