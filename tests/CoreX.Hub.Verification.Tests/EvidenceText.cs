namespace CoreX.Hub.Verification.Tests;

/// <summary>Reads stored blobs back so a test can assert on what was actually persisted.</summary>
internal static class EvidenceText
{
    public static string ReadAll(VerificationFixture fixture)
    {
        var blobs = fixture.BlobFiles();
        return blobs.Count == 0
            ? string.Empty
            : string.Join("\n", blobs.Select(File.ReadAllText));
    }

    /// <summary>
    /// Reads every byte under the blob root, so a search for a secret cannot miss because
    /// the value landed in an unexpected file.
    /// </summary>
    public static bool BlobTreeContains(VerificationFixture fixture, string needle)
    {
        foreach (var path in fixture.BlobFiles())
        {
            if (File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
