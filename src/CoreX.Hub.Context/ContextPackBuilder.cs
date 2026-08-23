using System.Security.Cryptography;
using System.Text;
using CoreX.Hub.Context.Abstractions;

namespace CoreX.Hub.Context;

/// <summary>
/// Materialises the context pack for one execution.
///
/// Deterministic by construction: the same semantic input produces the same bytes and the
/// same ContextPackHash, whatever the machine, the user, the process or the clock. Nothing
/// here reads the repository being worked on, and nothing here evaluates task truth - the
/// caller has already decided what is true and what is trusted.
/// </summary>
public sealed partial class ContextPackBuilder
{
    /// <summary>Hard budget from architecture H.5. Exceeding it fails; it never truncates.</summary>
    public const int MaxPackBytes = 64 * 1024;

    public const string SystemFileName = "system.md";
    public const string RulesFileName = "rules.md";
    public const string TaskFileName = "task.md";
    public const string RepositoryStateFileName = "repo-state.json";
    public const string ManifestFileName = "manifest.json";
    public const string DecisionsDirectoryName = "decisions";
    public const string PendingDirectoryName = "_pending";

    /// <summary>Bumped only when the canonical hash payload changes.</summary>
    public const string ManifestFormat = "corex-context-pack-v1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IContextPathGuard _paths;

    public ContextPackBuilder(IContextPathGuard paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>
    /// Test seam: invoked after the pending pack is complete and before it is published.
    /// Production leaves it null.
    /// </summary>
    public Func<CancellationToken, Task>? BeforePublishAsync { get; set; }

    public async Task<ContextBuildResult> BuildAsync(
        ContextBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return ContextBuildResult.Failed(ContextBuildFailure.InvalidRequest, invalid);
        }

        // Rendered first, in memory: the budget is a property of the semantic content, not
        // of whatever happens to reach the disk.
        var files = Render(request);
        var totalBytes = files.Sum(f => (long)f.Content.Length);

        if (totalBytes > MaxPackBytes)
        {
            return ContextBuildResult.Failed(
                ContextBuildFailure.BudgetExceeded,
                $"the pack is {totalBytes} bytes, above the {MaxPackBytes} byte budget; "
                + "nothing is truncated, summarised or dropped to make it fit");
        }

        var rootDecision = _paths.Validate(request.ContextRoot, request.ContextRoot, mustExist: true);
        if (!rootDecision.IsAllowed)
        {
            return ContextBuildResult.Failed(
                ContextBuildFailure.PathRejected, $"context root refused: {rootDecision.Reason}");
        }

        var root = rootDecision.CanonicalPath!;
        var packName = request.ExecutionId.ToString("D");

        var finalDecision = _paths.Validate(root, Path.Combine(root, packName), mustExist: false);
        if (!finalDecision.IsAllowed)
        {
            return ContextBuildResult.Failed(
                ContextBuildFailure.PathRejected, $"pack directory refused: {finalDecision.Reason}");
        }

        var finalPath = finalDecision.CanonicalPath!;

        if (Directory.Exists(finalPath))
        {
            return ContextBuildResult.Failed(
                ContextBuildFailure.PackAlreadyExists,
                $"a context pack already exists for execution {packName}");
        }

        var pendingPath = Path.Combine(root, PendingDirectoryName, packName + "-" + Guid.NewGuid().ToString("N"));

        try
        {
            var inventory = await MaterializeAsync(pendingPath, files, cancellationToken);
            var hash = ComputePackHash(inventory);

            await WriteManifestAsync(pendingPath, request, inventory, hash, totalBytes, cancellationToken);

            if (BeforePublishAsync is { } hook)
            {
                await hook(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // One rename publishes the whole pack: a reader never sees a half built one.
            Directory.CreateDirectory(root);
            Directory.Move(pendingPath, finalPath);

            return ContextBuildResult.Ok(new ContextPack
            {
                ExecutionId = request.ExecutionId,
                PackPath = finalPath,
                ContextPackHash = hash,
                Files = inventory,
                TotalBytes = totalBytes,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ContextBuildResult.Failed(ContextBuildFailure.MaterializationFailed, ex.Message);
        }
        finally
        {
            TryDeletePending(pendingPath);
        }
    }

    private static string? Validate(ContextBuildRequest request)
    {
        if (request.ExecutionId == Guid.Empty)
        {
            return "the request has no execution id";
        }

        if (string.IsNullOrWhiteSpace(request.ContextRoot))
        {
            return "the request has no context root";
        }

        if (string.IsNullOrWhiteSpace(request.TaskKey))
        {
            return "the request has no task key";
        }

        if (string.IsNullOrWhiteSpace(request.Objective))
        {
            return "the request has no objective";
        }

        var duplicate = request.Decisions
            .GroupBy(d => d.Slug, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        return duplicate is null ? null : $"decision '{duplicate.Key}' is supplied more than once";
    }

    private static void TryDeletePending(string pendingPath)
    {
        try
        {
            if (Directory.Exists(pendingPath))
            {
                Directory.Delete(pendingPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Debris outside the published area; it can never be mistaken for a pack.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
