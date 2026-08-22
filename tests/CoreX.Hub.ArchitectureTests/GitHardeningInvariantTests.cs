namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Structural guards for the git secondary-execution hardening.
///
/// These complement, and never replace, the behavioural marker tests in
/// CoreX.Hub.Workspace.Tests: those prove a repository controlled helper does not run,
/// while these make it hard to silently drop a flag later.
/// </summary>
public sealed class GitHardeningInvariantTests
{
    private const string WorkspaceManagerFileName = "WorkspaceManager.cs";
    private const string GitEnvironmentFileName = "GitEnvironment.cs";

    [Fact]
    public void Every_managed_diff_disables_external_diff_and_textconv()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        var diffLines = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("[\"diff\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(diffLines);

        foreach (var line in diffLines)
        {
            Assert.Contains("NoExternalDiff", line, StringComparison.Ordinal);
            Assert.Contains("NoTextConv", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_managed_git_runtime_neutralises_fsmonitor_and_signing()
    {
        var source = ReadProductionFile(GitEnvironmentFileName);

        Assert.Contains("(GitConfigKeys.FsMonitor, \"false\")", source, StringComparison.Ordinal);
        Assert.Contains("(GitConfigKeys.CommitGpgSign, \"false\")", source, StringComparison.Ordinal);
        Assert.Contains("core.fsmonitor", source, StringComparison.Ordinal);
        Assert.Contains("commit.gpgSign", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_safety_preflight_is_reachable_from_every_required_boundary()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);

        // Workspace creation goes through the full preflight.
        AssertMethodContains(source, "PrepareAsync", "await PreflightAsync(");

        // Evidence collection and staging revalidate, because config can change in between.
        AssertMethodContains(source, "CaptureChangesAsync", "RevalidateSafetyAsync(");
        AssertMethodContains(source, "CreateResultCommitAsync", "RevalidateSafetyAsync(");
    }

    [Fact]
    public void Staging_never_happens_before_the_safety_revalidation()
    {
        var source = ReadProductionFile(WorkspaceManagerFileName);
        var method = ExtractMethod(source, "CreateResultCommitAsync");

        var revalidate = method.IndexOf("RevalidateSafetyAsync(", StringComparison.Ordinal);
        var stage = method.IndexOf("[\"add\", \"-A\"]", StringComparison.Ordinal);

        Assert.True(revalidate >= 0, "CreateResultCommitAsync must revalidate");
        Assert.True(stage > 0, "CreateResultCommitAsync must still stage");
        Assert.True(revalidate < stage, "the safety revalidation must precede git add -A");
    }

    [Fact]
    public void The_unsupported_configuration_families_are_explicit_and_small()
    {
        // Deliberately a short hard-coded list, not a policy engine.
        Assert.Equal(
            ["filter.", "include.", "includeif."],
            Workspace.WorkspaceManager.UnsupportedConfigurationFamilies.Order(StringComparer.Ordinal));
    }

    private static string ReadProductionFile(string fileName)
    {
        var path = RepoLayout.ProductionSourceFiles
            .SingleOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal));

        Assert.NotNull(path);
        return File.ReadAllText(path);
    }

    private static void AssertMethodContains(string source, string methodName, string expected)
    {
        var method = ExtractMethod(source, methodName);
        Assert.Contains(expected, method, StringComparison.Ordinal);
    }

    /// <summary>
    /// Crude but sufficient: everything between a method signature and the next method
    /// signature at the same indentation.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{methodName} was not found");

        var next = source.IndexOf("\n    public ", start + 1, StringComparison.Ordinal);
        var privateNext = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);

        if (privateNext >= 0 && (next < 0 || privateNext < next))
        {
            next = privateNext;
        }

        return next < 0 ? source[start..] : source[start..next];
    }
}
