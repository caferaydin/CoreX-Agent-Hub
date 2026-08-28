using System.Runtime.Versioning;
using CoreX.Hub.Application.Orchestration;
using CoreX.Hub.Application.Review;

namespace CoreX.Hub.Composition.Orchestration;

/// <summary>
/// The five ports one execution runs through, chosen together or not at all.
///
/// This type exists because of the failure it makes impossible rather than the values it holds.
/// Routing a hard organization is five separate decisions - workspace, evidence, agent,
/// verification, review - and any one of them left on the host is a hole the other four do not
/// close. An agent perfectly contained whose verification runs on Windows has simply moved the
/// problem to the step that executes the repository's own test suite; a reviewer left outside has
/// moved it one step further.
///
/// So there is no way to assemble a mixed set. The two factories below each build all five from
/// one decision, and nothing else constructs one. A future change that isolates the agent and
/// forgets the reviewer does not compile rather than shipping quietly.
///
/// The flag is the last field deliberately: it is what the set already is, not an instruction
/// somebody could set differently from the implementations beside it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record ExecutionPortSet
{
    private ExecutionPortSet(
        IExecutionWorkspace workspace,
        IExecutionEvidenceWriter evidence,
        IAgentRuntimeGateway agent,
        IExecutionVerifier verifier,
        ReviewEngine? reviewer,
        bool isHard)
    {
        Workspace = workspace;
        Evidence = evidence;
        Agent = agent;
        Verifier = verifier;
        Reviewer = reviewer;
        IsHard = isHard;
    }

    public IExecutionWorkspace Workspace { get; }

    public IExecutionEvidenceWriter Evidence { get; }

    public IAgentRuntimeGateway Agent { get; }

    public IExecutionVerifier Verifier { get; }

    /// <summary>Null when the repository names no reviewer, which is a skip and never a pass.</summary>
    public ReviewEngine? Reviewer { get; }

    /// <summary>Whether these are the isolated implementations. Descriptive, never a switch.</summary>
    public bool IsHard { get; }

    /// <summary>
    /// The host implementations, for an organization that is not hard isolated.
    ///
    /// Unchanged from what a logical execution has always used. Nothing about a machine being
    /// capable of hard isolation moves a logical organization into it.
    /// </summary>
    public static ExecutionPortSet Logical(
        IExecutionWorkspace workspace,
        IExecutionEvidenceWriter evidence,
        IAgentRuntimeGateway agent,
        IExecutionVerifier verifier,
        ReviewEngine? reviewer) =>
        new(workspace, evidence, agent, verifier, reviewer, false);

    /// <summary>
    /// The isolated implementations, all of them.
    ///
    /// Every argument here is a hard adapter bound to the same execution environment, so the agent,
    /// the verification commands and the reviewer land in one namespace rather than three. There is
    /// no overload that takes four of them.
    /// </summary>
    public static ExecutionPortSet Hard(
        IExecutionWorkspace workspace,
        IExecutionEvidenceWriter evidence,
        IAgentRuntimeGateway agent,
        IExecutionVerifier verifier,
        ReviewEngine? reviewer) =>
        new(workspace, evidence, agent, verifier, reviewer, true);
}
