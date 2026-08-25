using System.Net;
using System.Runtime.Versioning;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// The properties that make an unauthenticated Hub defensible.
///
/// There is no identity here, no roles and no tokens, and that is a deliberate choice for a
/// single-user local tool. It is only a defensible one while the surface is unreachable from
/// anywhere else and cannot be talked into reading a file nobody asked for. Those two things
/// are what this file is about.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecurityTests
{
    [Fact]
    public void The_default_binding_is_loopback_and_nothing_else()
    {
        // The one setting that would make an Hub with no authentication reachable by someone
        // else. Asserted as a value rather than trusted to a launch profile that production
        // does not read.
        Assert.Equal("http://127.0.0.1:5177", new HubApiOptions().Bind());
        Assert.DoesNotContain("0.0.0.0", HubApiOptions.LoopbackUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("+", HubApiOptions.LoopbackUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5177")]
    [InlineData("http://+:5177")]
    [InlineData("http://*:5177")]
    [InlineData("http://192.168.1.10:5177")]
    [InlineData("https://hub.example.com")]
    public void An_address_that_is_not_loopback_is_refused_rather_than_bound(string url)
    {
        // Refused where it is stated, not reported later. A host that started and then
        // complained would already be listening.
        var refusal = Assert.Throws<ArgumentException>(() => new HubApiOptions { ListenUrl = url }.Bind());

        Assert.Contains("loopback", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:0")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://[::1]:5177")]
    public void Every_loopback_spelling_is_accepted(string url) =>
        Assert.Equal(url, new HubApiOptions { ListenUrl = url }.Bind());

    [Fact]
    public void Cross_origin_is_closed_unless_a_developer_opened_it_by_name()
    {
        // Empty by default means same-origin. There is no wildcard anywhere to find.
        Assert.Empty(new HubApiOptions().DevelopmentCorsOrigins);

        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CoreX.Hub.Api", "HubApi.cs"));

        Assert.DoesNotContain("AllowAnyOrigin", source, StringComparison.Ordinal);
        Assert.Contains("WithOrigins", source, StringComparison.Ordinal);

        // And the named list is only honoured in Development, so a stray origin in a
        // production configuration still serves same-origin.
        Assert.Contains("IsDevelopment()", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_is_addressed_by_identity_and_never_by_a_path()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        // No route anywhere takes a filesystem path, so there is nothing to traverse.
        var source = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src", "CoreX.Hub.Api"), "*.cs")
            .Select(File.ReadAllText)
            .ToList();

        Assert.All(source, s => Assert.DoesNotContain("{path}", s, StringComparison.Ordinal));
        Assert.All(source, s => Assert.DoesNotContain("{*path", s, StringComparison.Ordinal));
        Assert.All(source, s => Assert.DoesNotContain("Path.Combine(root", s, StringComparison.Ordinal));

        // The attempts a caller might make against the route that does exist.
        var task = await hub.SeedTaskAsync("API-300");
        var execution = await hub.SeedExecutionAsync(task, ExecutionOutcome.Passed);

        foreach (var attempt in new[]
                 {
                     "..%2F..%2Fhub.db",
                     "..\\..\\hub.db",
                     "C:%5CWindows%5Cwin.ini",
                 })
        {
            using var response = await hub.Client.GetAsync(
                $"/api/v1/executions/{execution.Id:D}/evidence/{attempt}/content");

            // The route only matches a guid, so none of these reach a handler at all.
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"{attempt} produced {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_evidence_id_from_another_execution_cannot_be_read_through_this_one()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        var mine = await hub.SeedTaskAsync("API-310");
        var other = await hub.SeedTaskAsync("API-311");

        var minesExecution = await hub.SeedExecutionAsync(mine, ExecutionOutcome.Passed);
        var othersExecution = await hub.SeedExecutionAsync(other, ExecutionOutcome.Passed);

        var othersEvidence = Guid.CreateVersion7();

        // Scoped by the execution that owns it, so a guessed or borrowed id is a 404 rather
        // than a read across executions.
        var (status, _) = await hub.GetAsync(
            $"/api/v1/executions/{minesExecution.Id:D}/evidence/{othersEvidence:D}/content");

        Assert.Equal(404, status);
        Assert.NotEqual(minesExecution.Id, othersExecution.Id);
    }

    [Fact]
    public async Task An_unexpected_failure_says_nothing_about_the_machine()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        // A body the model binder cannot make sense of: the framework's own failure path.
        using var response = await hub.Client.PostAsync(
            "/api/v1/tasks",
            new StringContent("{ not json at all", System.Text.Encoding.UTF8, "application/json"));

        var text = await response.Content.ReadAsStringAsync();

        Assert.True((int)response.StatusCode >= 400);

        // Whatever it says, it does not say where the Hub lives or what threw.
        foreach (var leak in new[] { "StackTrace", "at CoreX.Hub", ":\\", "Exception:" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_api_exposes_no_way_to_pick_a_writable_reviewer()
    {
        await using var hub = await HubApiFixture.CreateAsync();

        // Reporting a permission profile is fine and useful - the future UI has to show what
        // a profile is. Accepting one is not. So the assertion is over what a caller can
        // send, not over what the Hub can say.
        var requests = new[]
        {
            typeof(RunBody), typeof(CreateTaskBody), typeof(AcceptBody),
            typeof(OverrideBody), typeof(DecisionBody), typeof(ActorBody), typeof(CriterionBody),
        };

        foreach (var contract in requests)
        {
            foreach (var property in contract.GetProperties())
            {
                foreach (var forbidden in new[]
                         {
                             "Permission", "Sandbox", "Reviewer", "Runtime", "Tool", "Executable", "Path",
                         })
                {
                    Assert.False(
                        property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"{contract.Name}.{property.Name} would let a request choose {forbidden}");
                }
            }
        }

        // The run body carries an actor and an implementer, and nothing else. The catalogue
        // decides the reviewer, the permission profile and the sandbox - which is what keeps
        // a read-only reviewer from being asked to write over HTTP.
        Assert.Equal(
            ["Actor", "AgentProfile"],
            typeof(RunBody).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        // And no endpoint writes one of those into the request it builds.
        var endpoints = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src", "CoreX.Hub.Api"), "Endpoints.*.cs")
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(endpoints);

        foreach (var forbidden in new[] { "PermissionProfile =", "workspace-write", "ReviewerSelection" })
        {
            Assert.All(endpoints, s => Assert.DoesNotContain(forbidden, s, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string RepositoryRoot()
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

        throw new InvalidOperationException("no repository root");
    }
}
