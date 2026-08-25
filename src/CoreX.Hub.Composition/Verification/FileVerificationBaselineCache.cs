using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoreX.Hub.Verification.Abstractions;
using CoreX.Hub.Verification.Baseline;
using CoreX.Hub.Verification.Reports;

namespace CoreX.Hub.Composition.Verification;

/// <summary>
/// A small Hub owned baseline cache on disk:
///
///   &lt;data&gt;/cache/verification/&lt;key hash&gt;.json
///
/// It lives outside every worktree, so an agent cannot reach it through ordinary
/// repository operations. Entries are written atomically, so a reader never observes a
/// half written entry, and a torn or unreadable entry is treated as a miss rather than as
/// corrupt state - the worst case is that a baseline is recomputed.
///
/// There is no eviction: cleanup is Phase 10.
/// </summary>
public sealed class FileVerificationBaselineCache : IVerificationBaselineCache
{
    public const string CacheDirectoryName = "cache";
    public const string VerificationDirectoryName = "verification";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public FileVerificationBaselineCache(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory = Path.Combine(
            Path.GetFullPath(dataDirectory), CacheDirectoryName, VerificationDirectoryName);

        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string ResolveEntryPath(BaselineKey key) =>
        Path.Combine(Directory, key.CacheFileStem + ".json");

    public async Task<BaselineEntry?> TryGetAsync(BaselineKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var path = ResolveEntryPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, Utf8NoBom, cancellationToken);
            var record = JsonSerializer.Deserialize<CacheRecord>(json, JsonOptions);

            if (record is null || !record.Matches(key))
            {
                return null;
            }

            return new BaselineEntry
            {
                Key = key,
                StepName = record.StepName ?? string.Empty,
                CapturedAtUtc = record.CapturedAtUtc,
                Summary = new TestSummary
                {
                    Format = record.Format,
                    Total = record.Total,
                    Passed = record.Passed,
                    Failed = record.Failed,
                    Errors = record.Errors,
                    Skipped = record.Skipped,
                    Duration = record.DurationSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds)
                        : null,
                },
            };
        }
        catch (JsonException)
        {
            // A damaged entry is a miss, never a wrong baseline.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SaveAsync(BaselineEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = new CacheRecord
        {
            RepositoryId = entry.Key.RepositoryId,
            BaseCommitSha = entry.Key.BaseCommitSha,
            ProfileHash = entry.Key.ProfileHash,
            StepName = entry.StepName,
            CapturedAtUtc = entry.CapturedAtUtc,
            Format = entry.Summary.Format,
            Total = entry.Summary.Total,
            Passed = entry.Summary.Passed,
            Failed = entry.Summary.Failed,
            Errors = entry.Summary.Errors,
            Skipped = entry.Summary.Skipped,
            DurationSeconds = entry.Summary.Duration?.TotalSeconds,
        };

        var json = JsonSerializer.Serialize(record, JsonOptions);
        var finalPath = ResolveEntryPath(entry.Key);
        var pendingPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(json), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceAtomicallyAsync(pendingPath, finalPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(pendingPath))
            {
                try
                {
                    File.Delete(pendingPath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Replaces the entry with a single rename, so a concurrent reader sees either the old
    /// entry or the new one and never a partial write.
    ///
    /// Windows can transiently refuse the replace while another writer is renaming onto the
    /// same destination. That is a collision between two complete entries for the same key,
    /// not a corruption risk, so the rename is retried briefly and then conceded: whichever
    /// writer won left a complete entry behind.
    /// </summary>
    private static async Task ReplaceAtomicallyAsync(
        string pendingPath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        const int attempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(pendingPath, finalPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                {
                    if (File.Exists(finalPath))
                    {
                        return;
                    }

                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken);
            }
        }
    }

    private sealed class CacheRecord
    {
        public Guid RepositoryId { get; init; }

        public string BaseCommitSha { get; init; } = string.Empty;

        public string ProfileHash { get; init; } = string.Empty;

        public string? StepName { get; init; }

        public DateTimeOffset CapturedAtUtc { get; init; }

        public TestReportFormat Format { get; init; }

        public int Total { get; init; }

        public int Passed { get; init; }

        public int Failed { get; init; }

        public int Errors { get; init; }

        public int Skipped { get; init; }

        public double? DurationSeconds { get; init; }

        /// <summary>
        /// The file name already derives from the key, but the key is stored as well so a
        /// stale or hand edited file cannot be served for a different identity.
        /// </summary>
        public bool Matches(BaselineKey key) =>
            RepositoryId == key.RepositoryId
            && string.Equals(BaseCommitSha, key.BaseCommitSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ProfileHash, key.ProfileHash, StringComparison.OrdinalIgnoreCase);
    }
}
