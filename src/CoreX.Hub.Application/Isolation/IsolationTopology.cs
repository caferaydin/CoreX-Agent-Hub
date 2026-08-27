using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Application.Isolation;

/// <summary>One organization as the topology rule sees it.</summary>
/// <param name="Slug">Safe to print.</param>
/// <param name="DeclaredHard">Whether the operator declared hard isolation for it.</param>
/// <param name="CanStartExecution">Whether it may run repository controlled code at all.</param>
/// <param name="MachineReady">Whether its hard environment was actually established.</param>
public readonly record struct IsolationParticipant(
    string Slug,
    bool DeclaredHard,
    bool CanStartExecution,
    bool MachineReady);

/// <summary>
/// Whether the machine as a whole is arranged safely enough for a hard isolated organization
/// to run on it.
///
/// Per organization readiness cannot answer this, and that is the finding this type exists to
/// encode. Phase 10A proved it mechanically: a WSL distribution registered under a Windows
/// user is not a boundary against untrusted code running as that same Windows user, because
/// that code can simply ask for root inside the distro. So an environment can pass every check
/// about itself and still be exposed - by a neighbour.
///
/// The rule, therefore, is about everyone else:
///
///   if a hard organization is allowed to run, then every organization that can execute
///   repository controlled code must itself be hard and ready, or must not be able to execute.
///
/// It gates the hard organization rather than the neighbour. A logical organization keeps
/// working exactly as it did; what it loses is the ability to have a hard tenant beside it.
/// </summary>
public sealed record IsolationTopologyReadiness
{
    private IsolationTopologyReadiness(bool isReady, IReadOnlyList<string> problems)
    {
        IsReady = isReady;
        Problems = problems;
    }

    public bool IsReady { get; }

    /// <summary>Safe sentences naming the organizations that make the arrangement unsafe.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Evaluates the arrangement from the point of view of one hard organization.
    ///
    /// <paramref name="subject"/> is the organization that wants to run. Every other
    /// participant is examined for whether it could reach into that boundary.
    /// </summary>
    public static IsolationTopologyReadiness For(
        string subject,
        IEnumerable<IsolationParticipant> participants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(participants);

        var problems = new List<string>();

        foreach (var participant in participants)
        {
            if (string.Equals(participant.Slug, subject, StringComparison.Ordinal))
            {
                continue;
            }

            // A neighbour that cannot execute anything is not a threat to the boundary,
            // whatever it declares.
            if (!participant.CanStartExecution)
            {
                continue;
            }

            if (!participant.DeclaredHard)
            {
                problems.Add(
                    $"organization '{participant.Slug}' can execute repository controlled code without hard isolation");
                continue;
            }

            if (!participant.MachineReady)
            {
                // Declaring hard is not being hard. A neighbour whose environment was never
                // established is still running under the shared principal.
                problems.Add(
                    $"organization '{participant.Slug}' declares hard isolation but its environment is not ready");
            }
        }

        return new IsolationTopologyReadiness(problems.Count == 0, problems);
    }

    /// <summary>Builds a participant view from a catalogue organization and its readiness.</summary>
    public static IsolationParticipant Participant(Organization organization, bool machineReady) =>
        new(
            organization.Slug.Value,
            organization.IsolationLevel == IsolationLevel.Hard,
            organization.CanStartExecution,
            machineReady);
}
