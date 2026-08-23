namespace CoreX.Hub.Application.Gates;

/// <summary>
/// K1. The agent must not have committed.
///
/// The rule is one comparison and deliberately nothing more: HEAD equals the pinned base
/// commit, or it does not. HEAD is the authority. The agent is never asked whether it
/// committed, and its output is never read to find out - a runtime that misbehaved is
/// exactly the runtime whose account of itself cannot be trusted.
///
/// The gate reports. It never repairs: no reset, no branch -f, no update-ref, no amend, no
/// rebase, no force push. Whatever the agent committed stays reachable exactly where it
/// left it (ADR-0005, errata E1), because destroying the evidence of a policy violation to
/// tidy up the tree is the one recovery the Hub will not perform.
/// </summary>
public static class RuntimePolicyGate
{
    public const string UnexpectedAgentCommit = "UnexpectedAgentCommit";

    public static GateVerdict Evaluate(CapturedChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var moved = !string.Equals(
            changes.BaseCommitSha,
            changes.ObservedHeadSha,
            StringComparison.OrdinalIgnoreCase);

        if (!moved)
        {
            return GateVerdict.Pass(GateKind.RuntimePolicy);
        }

        // The commit graph enriches the report; it does not decide it. An unusual topology
        // that yields no listed commits still leaves HEAD somewhere other than the base,
        // and that alone is the violation. Nothing here manufactures a SHA to make the
        // list look convincing either.
        return GateVerdict.Block(
            GateKind.RuntimePolicy,
            $"{UnexpectedAgentCommit}: HEAD is {Short(changes.ObservedHeadSha)} "
            + $"but the workspace was pinned to {Short(changes.BaseCommitSha)}") with
        {
            UnexpectedAgentCommitDetected = true,
            UnexpectedCommitShas = changes.UnexpectedCommitShas,
        };
    }

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(12, sha.Length)];
}
