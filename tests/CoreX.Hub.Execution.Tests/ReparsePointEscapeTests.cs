using CoreX.Hub.Execution.FileSystem;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// A lexical under-root check is not enough: a junction inside the root can point
/// anywhere. These tests create real NTFS reparse points rather than mocking
/// FileAttributes.ReparsePoint.
/// </summary>
public sealed class ReparsePointEscapeTests
{
    [Fact]
    public void A_junction_that_leaves_the_root_is_rejected()
    {
        using var workspace = TempWorkspace.Create();
        using var outside = TempWorkspace.Create();

        var secret = Path.Combine(outside.Root, "secret.txt");
        File.WriteAllText(secret, "outside");

        var link = workspace.Combine("safe-link");
        WindowsJunction.Create(link, outside.Root);

        // The junction really is one, and lexically it looks like a child of the root.
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.StartsWith(workspace.Root, link, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(link, "secret.txt")));

        var linkItself = workspace.Guard.ValidateExisting(link);
        Assert.False(linkItself.IsAllowed);
        Assert.Equal(PathRejectionReason.ReparsePointEscape, linkItself.Reason);

        var throughLink = workspace.Guard.ValidateExisting(Path.Combine(link, "secret.txt"));
        Assert.False(throughLink.IsAllowed);
        Assert.Equal(PathRejectionReason.ReparsePointEscape, throughLink.Reason);

        var proposedThroughLink = workspace.Guard.ValidateProposed(Path.Combine(link, "new-file.txt"));
        Assert.False(proposedThroughLink.IsAllowed);
        Assert.Equal(PathRejectionReason.ReparsePointEscape, proposedThroughLink.Reason);

        EvidenceLog.Write($"ReparsePointEscape rejected: {link} -> {outside.Root}");
    }

    [Fact]
    public void A_junction_nested_deeper_inside_the_root_is_still_caught()
    {
        using var workspace = TempWorkspace.Create();
        using var outside = TempWorkspace.Create();

        var parent = workspace.CreateDirectory("a", "b");
        var link = Path.Combine(parent, "escape");
        WindowsJunction.Create(link, outside.Root);

        var result = workspace.Guard.ValidateProposed(Path.Combine(link, "c", "d.txt"));

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.ReparsePointEscape, result.Reason);
    }

    /// <summary>
    /// Documented behaviour for the safe case: a junction whose target stays inside the
    /// root is accepted, and the canonical path is the physically resolved one.
    /// </summary>
    [Fact]
    public void A_junction_whose_target_stays_inside_the_root_is_accepted_and_resolved()
    {
        using var workspace = TempWorkspace.Create();

        var real = workspace.CreateDirectory("real");
        File.WriteAllText(Path.Combine(real, "inside.txt"), "inside");

        var link = workspace.Combine("alias");
        WindowsJunction.Create(link, real);

        var linkItself = workspace.Guard.ValidateExisting(link);
        Assert.True(linkItself.IsAllowed);
        Assert.Equal(real, linkItself.CanonicalPath, ignoreCase: true);

        var throughLink = workspace.Guard.ValidateExisting(Path.Combine(link, "inside.txt"));
        Assert.True(throughLink.IsAllowed);
        Assert.Equal(Path.Combine(real, "inside.txt"), throughLink.CanonicalPath, ignoreCase: true);

        EvidenceLog.Write($"ReparsePoint inside root accepted and resolved: {link} -> {real}");
    }

    [Fact]
    public async Task A_working_directory_reached_through_an_escaping_junction_is_refused()
    {
        using var workspace = TempWorkspace.Create();
        using var outside = TempWorkspace.Create();

        var link = workspace.Combine("wd-link");
        WindowsJunction.Create(link, outside.Root);

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(30), "exit", "0") with
        {
            WorkingDirectory = link,
        };

        var result = await workspace.Runner.RunAsync(request, CancellationToken.None);

        Assert.Equal(Processes.ProcessTerminationReason.StartFailed, result.TerminationReason);
        Assert.Contains("ReparsePointEscape", result.StartFailureMessage!, StringComparison.Ordinal);
    }
}
