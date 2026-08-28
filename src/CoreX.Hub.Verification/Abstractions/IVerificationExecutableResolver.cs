using CoreX.Hub.Verification.Profiles;

namespace CoreX.Hub.Verification.Abstractions;

/// <summary>
/// Turns the logical executable name a verification step declares into one absolute path, in the
/// environment the step is actually going to run in.
///
/// This exists because the previous single answer silently assumed one environment. The allowlist
/// mapped a name to a path and then asked the host filesystem whether that path was there - two
/// jobs in one type, and the second is a question about a machine. For a hard isolated
/// organization the machine that runs the command is not the machine asking, so the combined
/// answer was wrong in the most dangerous possible direction: a Windows path resolved on Windows
/// and then handed to a Linux workload to execute.
///
/// So resolution is a port. What a name means is a property of the environment, and each
/// environment answers for itself. There is deliberately no shared fallback: an implementation that
/// could not answer would otherwise be tempted to borrow the other one's answer, which is how a
/// hard organization ends up naming a host binary.
///
/// It is asynchronous because one honest implementation has to ask another machine. Nothing about
/// the isolation mechanism appears here - a resolver is handed a name and returns a path.
/// </summary>
public interface IVerificationExecutableResolver
{
    /// <summary>
    /// The absolute path this name means here, or why it means nothing.
    ///
    /// Fail closed by construction: a name with no mapping, or a mapping that does not survive
    /// validation, resolves to nothing and no process is started. There is no search path, and no
    /// resolver tool is run on the caller's behalf.
    /// </summary>
    Task<ExecutableResolution> ResolveAsync(string? logicalName, CancellationToken cancellationToken);
}
