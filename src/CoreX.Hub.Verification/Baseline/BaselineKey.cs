using System.Security.Cryptography;
using System.Text;

namespace CoreX.Hub.Verification.Baseline;

/// <summary>
/// The accepted baseline cache identity: repository, the commit the work started from and
/// the semantic profile hash. Nothing else participates.
///
/// Deliberately not part of the key: task identifier, attempt, branch name and current
/// HEAD. A baseline keyed on any of those would either never hit or would be reused for a
/// different recipe (Phase 4 brief section 21).
/// </summary>
public sealed record BaselineKey
{
    public BaselineKey(Guid repositoryId, string baseCommitSha, string profileHash)
    {
        if (repositoryId == Guid.Empty)
        {
            throw new ArgumentException("A baseline key needs a repository.", nameof(repositoryId));
        }

        RepositoryId = repositoryId;
        BaseCommitSha = NormalizeSha(baseCommitSha, nameof(baseCommitSha), 40);
        ProfileHash = NormalizeSha(profileHash, nameof(profileHash), 64);
    }

    public Guid RepositoryId { get; }

    public string BaseCommitSha { get; }

    public string ProfileHash { get; }

    /// <summary>Canonical text the cache file name is derived from.</summary>
    public string CanonicalText =>
        $"repository={RepositoryId:D}\nbaseCommit={BaseCommitSha}\nprofile={ProfileHash}";

    /// <summary>Stable file safe identity for one cache entry.</summary>
    public string CacheFileStem =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText)));

    public string Describe() =>
        $"repository {RepositoryId:D} @ {BaseCommitSha[..Math.Min(12, BaseCommitSha.Length)]} / profile {ProfileHash[..12]}";

    private static string NormalizeSha(string? value, string parameter, int expectedLength)
    {
        var text = (value ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            throw new ArgumentException($"{parameter} must not be empty.", parameter);
        }

        if (text.Length != expectedLength || !text.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                $"{parameter} must be {expectedLength} hexadecimal characters.", parameter);
        }

        return text.ToLowerInvariant();
    }
}
