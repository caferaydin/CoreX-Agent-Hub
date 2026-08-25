using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace CoreX.Hub.Api.Tests;

/// <summary>
/// Serving the operator UI from the same process, and the two things that must never happen
/// while doing it.
///
/// An API path must never become a page. A route nobody mapped has to answer as an API route
/// does - because a client that asked for JSON and received a 200 carrying HTML will parse the
/// HTML, fail somewhere inside its own code, and report a problem that has nothing to do with
/// the actual one.
///
/// A missing file must never become a page either. A browser handed index.html in place of a
/// missing script executes HTML as JavaScript, and the real fault - a stale build, a
/// mispublished asset - stays invisible behind a syntax error.
///
/// Everything else is a browser route, which is the whole reason the fallback exists: a person
/// who refreshes on /tasks/CXH-001 has to get the application back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SpaHostingTests
{
    private const string Index = "<!doctype html><title>CoreX Agent Hub</title><div id=\"root\"></div>";

    [Fact]
    public async Task A_browser_route_is_answered_with_the_application()
    {
        await using var ui = await UiHost.CreateAsync();

        foreach (var route in new[] { "/", "/tasks", "/tasks/CXH-001", "/executions/01a039bf-4fa9-7f71", "/agents" })
        {
            using var response = await ui.Hub.Client.GetAsync(route);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<div id=\"root\">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_api_route_nobody_mapped_is_a_404_and_never_the_application()
    {
        await using var ui = await UiHost.CreateAsync();

        foreach (var path in new[]
                 {
                     "/api/v1/definitely-not-a-route",
                     "/api/v1/tasks/CXH-001/definitely-not-a-route",
                     "/api/nope",
                     "/api",
                 })
        {
            using var response = await ui.Hub.Client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            // The point is not the status - a page would be a 200 - but the body. Nothing
            // under /api ever carries a document.
            Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("<div id=\"root\">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_real_api_route_still_answers_with_its_own_body()
    {
        await using var ui = await UiHost.CreateAsync();

        var (status, health) = await ui.Hub.GetAsync("/api/v1/health");

        Assert.Equal(200, status);
        Assert.Equal("healthy", health.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_missing_asset_is_a_404_and_never_the_application()
    {
        await using var ui = await UiHost.CreateAsync();

        foreach (var asset in new[]
                 {
                     "/assets/index-nothashed.js",
                     "/assets/gone.css",
                     "/favicon.ico",
                     "/tasks/something.js",
                 })
        {
            using var response = await ui.Hub.Client.GetAsync(asset);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();

            // A browser told to execute this would execute HTML. That is the failure mode.
            Assert.DoesNotContain("<div id=\"root\">", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_real_asset_is_served_with_its_own_type_and_may_be_cached()
    {
        await using var ui = await UiHost.CreateAsync();

        using var script = await ui.Hub.Client.GetAsync("/assets/index-abc12345.js");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);

        // Hashed names make a changed file a different URL, which is what makes this safe.
        Assert.True(script.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), script.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task The_document_that_names_the_bundles_is_never_cached_for_long()
    {
        await using var ui = await UiHost.CreateAsync();

        foreach (var route in new[] { "/", "/tasks", "/index.html" })
        {
            using var response = await ui.Hub.Client.GetAsync(route);

            Assert.True(
                response.Headers.CacheControl?.NoCache,
                $"{route} may not be cached: it is what names the current build.");
        }
    }

    [Fact]
    public async Task A_host_with_no_build_serves_the_api_and_no_pages_at_all()
    {
        // The supported configuration every API test runs in. A missing UI must degrade to
        // "no pages" rather than to a startup failure that hides which half is broken.
        await using var hub = await HubApiFixture.CreateAsync(serveOperatorUi: false);

        var (status, _) = await hub.GetAsync("/api/v1/health");

        Assert.Equal(200, status);

        using var page = await hub.Client.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Serving_the_ui_does_not_move_the_host_off_loopback()
    {
        await using var ui = await UiHost.CreateAsync();

        var address = new Uri(ui.Hub.Client.BaseAddress!.ToString());

        Assert.True(address.IsLoopback, address.ToString());

        // And the option still refuses to express anything else, UI or no UI.
        Assert.Throws<ArgumentException>(() =>
            new HubApiOptions { ListenUrl = "http://0.0.0.0:5177" }.Bind());

        Assert.Throws<ArgumentException>(() =>
            new HubApiOptions { ListenUrl = "http://10.0.0.5:5177", WebRoot = ui.WebRoot }.Bind());
    }

    [Fact]
    public void A_directory_without_an_index_is_not_a_ui_build()
    {
        var empty = Path.Combine(Path.GetTempPath(), "corex-ui-empty", Guid.CreateVersion7().ToString("N"));

        Directory.CreateDirectory(empty);

        try
        {
            Assert.Null(SpaHosting.Resolve(empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// A Hub with a UI build in front of it.
    ///
    /// The files are a stand-in for a Vite output rather than the real one: these tests are
    /// about what the host does with a directory of assets, and depending on a completed npm
    /// build would make the backend suite fail for a frontend reason.
    /// </summary>
    private sealed class UiHost : IAsyncDisposable
    {
        private UiHost(HubApiFixture hub, string webRoot)
        {
            Hub = hub;
            WebRoot = webRoot;
        }

        public HubApiFixture Hub { get; }

        public string WebRoot { get; }

        public static async Task<UiHost> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "corex-ui", Guid.CreateVersion7().ToString("N"));

            Directory.CreateDirectory(Path.Combine(root, "assets"));

            File.WriteAllText(Path.Combine(root, SpaHosting.IndexFileName), Index, new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "assets", "index-abc12345.js"),
                "export const hub = 1;\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(root, "assets", "index-abc12345.css"),
                ":root { color: inherit; }\n",
                new UTF8Encoding(false));

            return new UiHost(await HubApiFixture.CreateAsync(webRoot: root), root);
        }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();

            try
            {
                Directory.Delete(WebRoot, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives one test run is not a failure to report.
            }
        }
    }
}
