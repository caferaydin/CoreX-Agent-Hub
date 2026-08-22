using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Domain.Catalog;

namespace CoreX.Hub.Verification.Profiles;

/// <summary>
/// Deterministic identity of a verification recipe.
///
/// The hash covers what changes the meaning of a run - working directory, step order,
/// logical executables, arguments, timeouts, required flags and parser configuration -
/// and nothing incidental such as timestamps, identifiers or profile names.
/// </summary>
public static class ProfileHasher
{
    /// <summary>Bumped only if the canonical form itself changes, which invalidates every cached baseline.</summary>
    public const string CanonicalFormVersion = "corex-verification-profile-v1";

    public static string Compute(VerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ComputeFromCanonicalText(BuildCanonicalText(profile));
    }

    /// <summary>Exposed so a test can assert exactly what is and is not hashed.</summary>
    public static string BuildCanonicalText(VerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var builder = new StringBuilder();
        builder.Append(CanonicalFormVersion).Append('\n');
        builder.Append("workingDirectory=").Append(Normalize(profile.WorkingDirectory)).Append('\n');

        // Step order is part of the identity: the same steps in another order are another
        // recipe, so the sequence is written as given and never sorted.
        foreach (var step in profile.Steps)
        {
            builder.Append("step=").Append(step.Name).Append('\n');
            builder.Append("  executable=").Append(step.Executable).Append('\n');
            builder.Append("  timeoutSeconds=")
                .Append(step.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("  required=").Append(step.Required ? "true" : "false").Append('\n');
            builder.Append("  resultParser=").Append(step.ResultParser.ToString()).Append('\n');
            builder.Append("  resultPath=").Append(Normalize(step.ResultPath ?? string.Empty)).Append('\n');

            foreach (var argument in step.Args)
            {
                builder.Append("  arg=").Append(argument).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static string ComputeFromCanonicalText(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// Directory separators differ between how a profile is written and how it is read
    /// back, but they do not change the recipe.
    /// </summary>
    private static string Normalize(string value) =>
        value.Replace('\\', '/').Trim();
}
