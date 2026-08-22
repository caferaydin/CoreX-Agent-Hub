using CoreX.Hub.Execution.FileSystem;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// The managed execution filesystem trust boundary (architecture E.1 #2, K.3 #7).
/// Real temporary directories only; no repository and no company data is involved.
/// </summary>
public sealed class FileSystemGuardTests
{
    [Fact]
    public void The_root_itself_is_accepted_and_canonicalised()
    {
        using var workspace = TempWorkspace.Create();

        var result = workspace.Guard.ValidateExisting(workspace.Root);

        Assert.True(result.IsAllowed);
        Assert.Equal(workspace.Root, result.CanonicalPath);
    }

    [Fact]
    public void A_direct_child_is_accepted()
    {
        using var workspace = TempWorkspace.Create();
        var child = workspace.CreateDirectory("child");

        var result = workspace.Guard.ValidateExisting(child);

        Assert.True(result.IsAllowed);
        Assert.Equal(child, result.CanonicalPath);
    }

    [Fact]
    public void A_deep_child_file_is_accepted()
    {
        using var workspace = TempWorkspace.Create();
        var file = workspace.CreateFile(Path.Combine("a", "b", "c", "leaf.txt"));

        var result = workspace.Guard.ValidateExisting(file);

        Assert.True(result.IsAllowed);
        Assert.Equal(file, result.CanonicalPath);
    }

    [Fact]
    public void A_relative_candidate_resolves_against_the_root_not_the_working_directory()
    {
        using var workspace = TempWorkspace.Create();
        var child = workspace.CreateDirectory("relative-child");

        var result = workspace.Guard.ValidateExisting("relative-child");

        Assert.True(result.IsAllowed);
        Assert.Equal(child, result.CanonicalPath);
    }

    [Fact]
    public void A_parent_traversal_escape_is_rejected()
    {
        using var workspace = TempWorkspace.Create();

        var result = workspace.Guard.ValidateProposed(Path.Combine(workspace.Root, "..", "outside"));

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.TraversalEscape, result.Reason);
    }

    [Fact]
    public void Mixed_dot_segments_are_canonicalised_before_the_decision()
    {
        using var workspace = TempWorkspace.Create();
        var inside = workspace.CreateDirectory("x", "y");

        var stillInside = workspace.Guard.ValidateExisting(
            Path.Combine(workspace.Root, "x", ".", "z", "..", "y"));

        Assert.True(stillInside.IsAllowed);
        Assert.Equal(inside, stillInside.CanonicalPath);

        var escapes = workspace.Guard.ValidateProposed(
            Path.Combine(workspace.Root, "x", "..", "..", "elsewhere"));

        Assert.False(escapes.IsAllowed);
        Assert.Equal(PathRejectionReason.TraversalEscape, escapes.Reason);
    }

    [Fact]
    public void An_absolute_path_outside_the_root_is_rejected()
    {
        using var workspace = TempWorkspace.Create();
        using var other = TempWorkspace.Create();

        var result = workspace.Guard.ValidateExisting(other.Root);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.OutsideRoot, result.Reason);
    }

    /// <summary>
    /// A plain StartsWith comparison would accept this: the sibling name merely begins
    /// with the root's name.
    /// </summary>
    [Fact]
    public void A_sibling_whose_name_shares_the_root_prefix_is_rejected()
    {
        using var parent = TempWorkspace.Create();
        var root = parent.CreateDirectory("corex");
        var sibling = parent.CreateDirectory("corex-evil");
        var siblingFile = Path.Combine(sibling, "secret.txt");
        File.WriteAllText(siblingFile, "x");

        var guard = FileSystemGuard.Create(root);

        Assert.StartsWith(root, sibling, StringComparison.OrdinalIgnoreCase);

        var directory = guard.ValidateExisting(sibling);
        Assert.False(directory.IsAllowed);
        Assert.Equal(PathRejectionReason.OutsideRoot, directory.Reason);

        var file = guard.ValidateExisting(siblingFile);
        Assert.False(file.IsAllowed);
        Assert.Equal(PathRejectionReason.OutsideRoot, file.Reason);
    }

    [Fact]
    public void Windows_path_case_variation_still_resolves_inside_the_root()
    {
        using var workspace = TempWorkspace.Create();
        var child = workspace.CreateDirectory("CaseChild");

        var result = workspace.Guard.ValidateExisting(child.ToUpperInvariant());

        Assert.True(result.IsAllowed);
        Assert.Equal(child, result.CanonicalPath, ignoreCase: true);
    }

    [Fact]
    public void A_safe_child_that_does_not_exist_yet_can_be_validated()
    {
        using var workspace = TempWorkspace.Create();
        workspace.CreateDirectory("existing");
        var proposed = workspace.Combine("existing", "not-created-yet", "file.txt");

        var asProposed = workspace.Guard.ValidateProposed(proposed);
        Assert.True(asProposed.IsAllowed);
        Assert.Equal(proposed, asProposed.CanonicalPath);

        var asExisting = workspace.Guard.ValidateExisting(proposed);
        Assert.False(asExisting.IsAllowed);
        Assert.Equal(PathRejectionReason.MissingRequiredPath, asExisting.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_rejected(string candidate)
    {
        using var workspace = TempWorkspace.Create();

        var result = workspace.Guard.ValidateProposed(candidate);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.InvalidPath, result.Reason);
    }

    [Fact]
    public void Unc_and_device_paths_are_unsupported()
    {
        using var workspace = TempWorkspace.Create();

        // Built from the separator char so the literal cannot be mangled by tooling.
        var sep = Path.DirectorySeparatorChar;

        var unc = workspace.Guard.ValidateProposed($"{sep}{sep}server{sep}share{sep}file.txt");
        Assert.False(unc.IsAllowed);
        Assert.Equal(PathRejectionReason.UnsupportedPath, unc.Reason);

        var device = workspace.Guard.ValidateProposed($"{sep}{sep}?{sep}D:{sep}cx");
        Assert.False(device.IsAllowed);
        Assert.Equal(PathRejectionReason.UnsupportedPath, device.Reason);
    }

    [Fact]
    public void A_relative_trust_root_is_rejected_rather_than_resolved_against_the_working_directory()
    {
        var result = FileSystemGuard.TryCreate("relative-root", out var guard);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.MissingRoot, result.Reason);
        Assert.Null(guard);
    }

    [Fact]
    public void A_missing_trust_root_is_rejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "corex-missing-" + Guid.CreateVersion7().ToString("N"));

        var result = FileSystemGuard.TryCreate(missing, out var guard);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.MissingRoot, result.Reason);
        Assert.Null(guard);
    }

    [Fact]
    public void An_empty_trust_root_is_rejected()
    {
        var result = FileSystemGuard.TryCreate("  ", out var guard);

        Assert.False(result.IsAllowed);
        Assert.Equal(PathRejectionReason.MissingRoot, result.Reason);
        Assert.Null(guard);
    }

    [Fact]
    public void Require_throws_with_the_recorded_reason()
    {
        using var workspace = TempWorkspace.Create();
        using var other = TempWorkspace.Create();

        var error = Assert.Throws<PathTrustException>(() => workspace.Guard.ValidateExisting(other.Root).Require());

        Assert.Equal(PathRejectionReason.OutsideRoot, error.Reason);
    }
}
