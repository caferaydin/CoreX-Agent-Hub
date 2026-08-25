using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Execution.FileSystem;
using CoreX.Hub.Verification.Abstractions;

namespace CoreX.Hub.Composition.Verification;

/// <summary>
/// Composition layer adapter that lets the verification module reach FileSystemGuard
/// without referencing the execution module (E.1 #2 and E.1 #4 both hold).
///
/// Every path a verification run touches - the working directory and each declared report
/// path - passes through here, so a traversal or a reparse point leaving the trust root is
/// rejected rather than normalised into something plausible.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileSystemGuardVerificationFileReader : IVerificationFileReader
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentDictionary<string, FileSystemGuard?> _guards =
        new(StringComparer.OrdinalIgnoreCase);

    public VerificationPathDecision ResolveUnderRoot(string trustRoot, string relativePath, bool mustExist)
    {
        var guard = _guards.GetOrAdd(trustRoot, root =>
        {
            FileSystemGuard.TryCreate(root, out var created);
            return created;
        });

        if (guard is null)
        {
            return VerificationPathDecision.Rejected($"trust root '{trustRoot}' is not usable");
        }

        var candidate = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(guard.TrustRoot, relativePath);

        var result = mustExist ? guard.ValidateExisting(candidate) : guard.ValidateProposed(candidate);

        return result.IsAllowed
            ? VerificationPathDecision.Allowed(result.CanonicalPath!)
            : VerificationPathDecision.Rejected($"{result.Reason}: {result.Detail}");
    }

    public async Task<VerificationReadResult> ReadTextAsync(
        string absolutePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
            {
                return VerificationReadResult.Failed(
                    VerificationReadFailure.NotFound, 0, $"'{absolutePath}' does not exist");
            }

            if (info.Length > maxBytes)
            {
                // Refused by size before a single byte is read into memory.
                return VerificationReadResult.Failed(
                    VerificationReadFailure.TooLarge,
                    info.Length,
                    $"the report is {info.Length} bytes, above the {maxBytes} byte ceiling");
            }

            var text = await File.ReadAllTextAsync(absolutePath, Utf8, cancellationToken);
            return VerificationReadResult.Ok(text, info.Length);
        }
        catch (IOException ex)
        {
            return VerificationReadResult.Failed(VerificationReadFailure.Unreadable, 0, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return VerificationReadResult.Failed(VerificationReadFailure.Unreadable, 0, ex.Message);
        }
    }
}
