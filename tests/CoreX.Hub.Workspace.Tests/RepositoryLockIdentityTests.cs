namespace CoreX.Hub.Workspace.Tests;

/// <summary>
/// Two calls addressing the same physical repository must resolve to one lock, and two
/// different repositories must not share one. Nothing here is a distributed lock: the
/// primitive stays a per repository semaphore in this process.
/// </summary>
public sealed class RepositoryLockIdentityTests
{
    [Theory]
    [InlineData(@"D:\Repo", @"d:\repo")]
    [InlineData(@"D:\Repo", @"D:\Repo\")]
    [InlineData(@"D:\Repo", @"D:/Repo")]
    [InlineData(@"D:\Repo", @"D:\REPO\\")]
    [InlineData(@"D:\Repo", @"  D:\Repo  ")]
    [InlineData(@"D:\a\b", @"D:/a/b/")]
    public void Equivalent_spellings_of_one_repository_share_a_lock(string first, string second)
    {
        var left = WorkspaceManager.RepositoryLockKey(first);
        var right = WorkspaceManager.RepositoryLockKey(second);

        // The dictionary comparer is case insensitive, so the keys have to match under it.
        Assert.Equal(left, right, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"D:\Repo", @"D:\Repo2")]
    [InlineData(@"D:\a\b", @"D:\a\c")]
    [InlineData(@"D:\Repo", @"E:\Repo")]
    public void Different_repositories_do_not_share_a_lock(string first, string second)
    {
        Assert.NotEqual(
            WorkspaceManager.RepositoryLockKey(first),
            WorkspaceManager.RepositoryLockKey(second),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_drive_root_keeps_its_separator()
    {
        // Trimming here would turn "D:\" into the drive relative "D:", which is a
        // different location.
        Assert.Equal(@"D:\", WorkspaceManager.RepositoryLockKey(@"D:\"));
    }

    [Fact]
    public void An_empty_repository_path_is_refused_rather_than_sharing_a_blank_lock()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceManager.RepositoryLockKey("   "));
    }
}
