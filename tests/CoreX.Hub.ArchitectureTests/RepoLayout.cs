namespace CoreX.Hub.ArchitectureTests;

/// <summary>Locates the repository so architecture tests can scan real source files.</summary>
internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    public static string SourceRoot => Path.Combine(Root, "src");

    /// <summary>
    /// Production source files only. Generated EF migrations are excluded: they are
    /// tool output, not hand written code subject to the coding invariants.
    /// </summary>
    public static IReadOnlyList<string> ProductionSourceFiles { get; } =
        [.. Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    public static string TestRoot => Path.Combine(Root, "tests");

    /// <summary>
    /// Test source files, for the few invariants whose subject is the harness itself.
    ///
    /// A test suite can be unsafe in ways production cannot: it is the part of the repository
    /// allowed to touch a real machine, so which machine it is allowed to touch is a property worth
    /// guarding in the same way everything else here is.
    /// </summary>
    public static IReadOnlyList<string> TestSourceFiles { get; } =
        [.. Directory.EnumerateFiles(TestRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    public static string Relative(string path) => Path.GetRelativePath(Root, path);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreX.Hub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (CoreX.Hub.slnx).");
    }
}
