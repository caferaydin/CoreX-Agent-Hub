using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// ADR-0007. The child environment starts empty; only explicitly allowlisted names are
/// copied, and the Hub forces its own values on top.
///
/// Every assertion inspects what the CHILD actually observed, not an internal dictionary.
/// </summary>
[Collection(EnvironmentIsolationCollection.Name)]
public sealed class EnvironmentIsolationTests
{
    private const string SecretName = "COREX_TEST_SECRET";

    [Fact]
    public async Task A_parent_secret_is_invisible_unless_it_is_allowlisted()
    {
        var secret = "s3cr3t-" + Guid.CreateVersion7().ToString("N");
        using var parentVariable = new ScopedEnvironmentVariable(SecretName, secret);
        using var workspace = TempWorkspace.Create();

        var withoutAllowlist = await ChildEnvironment.ReadAsync(workspace, Fixture.DotnetHostAllowlist);

        Assert.False(withoutAllowlist.ContainsKey(SecretName));
        Assert.DoesNotContain(secret, string.Join('\n', withoutAllowlist.Values), StringComparison.Ordinal);

        var withAllowlist = await ChildEnvironment.ReadAsync(
            workspace,
            [.. Fixture.DotnetHostAllowlist, SecretName]);

        Assert.True(withAllowlist.TryGetValue(SecretName, out var observed));
        Assert.Equal(secret, observed);
    }

    [Fact]
    public async Task Forced_hub_values_are_always_present()
    {
        using var workspace = TempWorkspace.Create();

        var child = await ChildEnvironment.ReadAsync(workspace, Fixture.DotnetHostAllowlist);

        Assert.Equal("0", child["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("1", child["NO_COLOR"]);
        Assert.Equal("1", child["CI"]);

        // GIT_ASKPASS points at a path that does not exist, so git cannot obtain
        // credentials interactively and no shell is involved.
        Assert.True(child.TryGetValue("GIT_ASKPASS", out var askPass));
        Assert.EndsWith(ProcessRunner.DisabledAskPassFileName, askPass, StringComparison.Ordinal);
        Assert.False(File.Exists(askPass));
    }

    [Fact]
    public async Task Forced_values_cannot_be_overridden_by_the_caller()
    {
        using var workspace = TempWorkspace.Create();

        var child = await ChildEnvironment.ReadAsync(
            workspace,
            Fixture.DotnetHostAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NO_COLOR"] = "0",
                ["CI"] = "false",
            });

        Assert.Equal("1", child["NO_COLOR"]);
        Assert.Equal("1", child["CI"]);
    }

    [Fact]
    public async Task Explicit_additional_values_reach_the_child()
    {
        using var workspace = TempWorkspace.Create();

        var child = await ChildEnvironment.ReadAsync(
            workspace,
            Fixture.DotnetHostAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COREX_EXPLICIT"] = "explicit-value",
            });

        Assert.Equal("explicit-value", child["COREX_EXPLICIT"]);
    }

    [Fact]
    public async Task Environment_does_not_leak_between_two_runs()
    {
        var secret = "leak-" + Guid.CreateVersion7().ToString("N");
        using var parentVariable = new ScopedEnvironmentVariable(SecretName, secret);
        using var workspace = TempWorkspace.Create();

        var permissive = await ChildEnvironment.ReadAsync(
            workspace,
            [.. Fixture.DotnetHostAllowlist, SecretName],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["COREX_RUN_ONE"] = "1" });

        Assert.Equal(secret, permissive[SecretName]);
        Assert.True(permissive.ContainsKey("COREX_RUN_ONE"));

        var restricted = await ChildEnvironment.ReadAsync(workspace, Fixture.DotnetHostAllowlist);

        Assert.False(restricted.ContainsKey(SecretName));
        Assert.False(restricted.ContainsKey("COREX_RUN_ONE"));
    }

    [Fact]
    public async Task Running_a_child_does_not_mutate_the_parent_environment()
    {
        using var workspace = TempWorkspace.Create();
        var before = Environment.GetEnvironmentVariables().Count;

        await ChildEnvironment.ReadAsync(
            workspace,
            Fixture.DotnetHostAllowlist,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["COREX_CHILD_ONLY"] = "yes" });

        Assert.Null(Environment.GetEnvironmentVariable("COREX_CHILD_ONLY"));
        Assert.Equal(before, Environment.GetEnvironmentVariables().Count);
    }

    [Fact]
    public async Task Allowlist_names_are_matched_case_insensitively_on_windows()
    {
        var secret = "case-" + Guid.CreateVersion7().ToString("N");
        using var parentVariable = new ScopedEnvironmentVariable(SecretName, secret);
        using var workspace = TempWorkspace.Create();

        var child = await ChildEnvironment.ReadAsync(
            workspace,
            [.. Fixture.DotnetHostAllowlist, "corex_test_secret"]);

        Assert.True(child.TryGetValue(SecretName, out var observed));
        Assert.Equal(secret, observed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BAD=NAME")]
    public void Malformed_environment_names_are_rejected(string name)
    {
        using var workspace = TempWorkspace.Create();

        var request = Fixture.Request(workspace, TimeSpan.FromSeconds(10), "print-env") with
        {
            EnvironmentAllowlist = [name],
        };

        Assert.Throws<ProcessRequestException>(request.Validate);
    }

    private sealed class ScopedEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public ScopedEnvironmentVariable(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentIsolationCollection
{
    public const string Name = "environment-isolation";
}
