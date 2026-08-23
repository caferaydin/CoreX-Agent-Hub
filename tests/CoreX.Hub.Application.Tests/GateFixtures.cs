using CoreX.Hub.Application.Gates;
using CoreX.Hub.Infrastructure.Evidence;

namespace CoreX.Hub.Application.Tests;

/// <summary>Builds the machine facts the gates evaluate.</summary>
internal static class GateFixtures
{
    public const string Base = "1111111111111111111111111111111111111111";

    public const string Moved = "2222222222222222222222222222222222222222";

    /// <summary>The gate wired to the one shared secret catalog, exactly as production does.</summary>
    public static SecretGuard SecretGuard { get; } = new(new CatalogSecretScanner());

    public static ExecutionSafetyGates Gates { get; } = new(SecretGuard);

    public static CapturedChanges Changes(
        string? head = null,
        IReadOnlyList<string>? changedPaths = null,
        IReadOnlyList<AddedLine>? addedLines = null,
        IReadOnlyList<string>? unexpectedCommits = null) => new()
    {
        BaseCommitSha = Base,
        ObservedHeadSha = head ?? Base,
        ChangedPaths = changedPaths ?? [],
        AddedLines = addedLines ?? [],
        UnexpectedCommitShas = unexpectedCommits ?? [],
    };

    public static IReadOnlyList<AddedLine> Added(string path, params string[] lines) =>
        [.. lines.Select((text, i) => new AddedLine(path, i + 1, text))];
}
