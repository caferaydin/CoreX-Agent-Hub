namespace CoreX.Hub.Workspace.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitHelperCollection
{
    public const string Name = "git-helper-invocation";
}

/// <summary>
/// Observes a helper git invokes as a bare executable path, where neither arguments nor
/// environment can carry a marker location. The fixture records itself next to its own
/// executable instead.
/// </summary>
internal static class HelperInvocationProbe
{
    private static string MarkerPath => Path.Combine(AppContext.BaseDirectory, "corex-helper-invoked.txt");

    public static bool WasInvoked => File.Exists(MarkerPath);

    public static void Clear()
    {
        if (File.Exists(MarkerPath))
        {
            File.Delete(MarkerPath);
        }
    }
}
