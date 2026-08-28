using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Verification.Profiles;

/// <summary>
/// Resolution for a verification run that happens on this machine.
///
/// A thin adapter over <see cref="ExecutableAllowlist"/> and nothing more, deliberately. The
/// allowlist's rules are the ones a logical execution has always been measured against - the
/// configured map is the only source, the search path is never consulted, and a mapping whose file
/// is not there is refused before anything starts - and none of that changes because hard isolation
/// now exists beside it.
///
/// Synchronous work behind an asynchronous port, which is the honest shape here: the host is the
/// machine asking, so the answer needs no round trip. It is completed rather than blocked on, so
/// nothing here occupies a thread waiting for itself.
/// </summary>
public sealed class HostVerificationExecutableResolver : IVerificationExecutableResolver
{
    private readonly ExecutableAllowlist _allowlist;

    public HostVerificationExecutableResolver(ExecutableAllowlist allowlist) =>
        _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));

    public Task<ExecutableResolution> ResolveAsync(string? logicalName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_allowlist.Resolve(logicalName));
    }
}
