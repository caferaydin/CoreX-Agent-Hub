using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace CoreX.Hub.Api;

/// <summary>
/// Serving the built operator UI from the same process, on the same origin, on one port.
///
/// The point is not convenience. Same-origin means the browser and the API agree about what
/// "here" is without anyone relaxing a cross-origin policy to make development comfortable,
/// and one process means an operator starts the Hub rather than starting the Hub and then a
/// second server that has to keep pointing at it.
///
/// Two rules make this safe rather than merely working, and both are enforced here instead of
/// being left to route-matching precedence.
///
/// An API path never becomes a page. A request under <see cref="ApiSegment"/> skips this
/// branch entirely, so an endpoint nobody mapped answers with an ordinary 404 and not with a
/// 200 carrying HTML. A client that asked for JSON and was handed a document would parse the
/// document, fail somewhere inside, and report the wrong thing.
///
/// A missing asset never becomes a page either. Anything that names a file - a hashed bundle,
/// a stylesheet, an icon - is answered by the static file middleware or not at all. Returning
/// index.html for a missing script is the failure mode where a browser executes HTML as
/// JavaScript and the real problem, a stale or misplaced build, stays invisible.
///
/// Everything else is a browser route. <c>/tasks</c>, <c>/tasks/CXH-001</c> and
/// <c>/executions/{id}</c> are places in a single page application, so a direct navigation or
/// a refresh has to reach the document that knows how to render them.
/// </summary>
public static class SpaHosting
{
    /// <summary>The prefix that is never a page. Matched as a segment, not as a prefix string.</summary>
    public const string ApiSegment = "/api";

    /// <summary>Where a published host keeps the built UI, beside its own executable.</summary>
    public const string DefaultDirectoryName = "wwwroot";

    /// <summary>Points a host at a UI build without rebuilding the host.</summary>
    public const string WebRootEnvironmentVariable = "COREX_HUB_WEB_ROOT";

    /// <summary>The document every browser route resolves to.</summary>
    public const string IndexFileName = "index.html";

    /// <summary>
    /// Which directory holds the built UI, or null when this host has none.
    ///
    /// A host with no UI is a normal, supported thing: the API is complete on its own, every
    /// test host runs that way, and a missing build must degrade to "no pages" rather than to
    /// a startup failure that hides which of the two halves is actually broken.
    /// </summary>
    public static string? Resolve(string? explicitRoot)
    {
        var candidates = new[]
        {
            explicitRoot,
            Environment.GetEnvironmentVariable(WebRootEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, DefaultDirectoryName),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var full = Path.GetFullPath(candidate);

            // The index is the test, not the directory: an empty wwwroot that survived a
            // failed build would otherwise serve nothing and say nothing about why.
            if (File.Exists(Path.Combine(full, IndexFileName)))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds the UI branch, if there is a UI to add.
    ///
    /// Placed before anything that could answer an API request, and skipped entirely for API
    /// paths, so the branch can never be the reason an endpoint behaved differently.
    /// </summary>
    public static void UseOperatorUi(this WebApplication app, string? webRoot)
    {
        ArgumentNullException.ThrowIfNull(app);

        var root = Resolve(webRoot);

        if (root is null)
        {
            return;
        }

        var files = new PhysicalFileProvider(root);
        var index = Path.Combine(root, IndexFileName);

        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(ApiSegment),
            branch =>
            {
                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,

                    // Only what a browser needs, and nothing guessed. An unknown extension is
                    // not served at all rather than served as a type the browser will sniff.
                    ContentTypeProvider = new FileExtensionContentTypeProvider(),
                    ServeUnknownFileTypes = false,
                    OnPrepareResponse = Cache,
                });

                branch.Use(async (context, next) =>
                {
                    if (!WantsAPage(context.Request))
                    {
                        // A missing file, a POST, or something this branch has no answer for.
                        // Rejoins the pipeline, where nothing matches and the result is a 404.
                        await next(context);
                        return;
                    }

                    // index.html is never cached for long: it is the one file whose whole job
                    // is naming the current hashed bundles, and a stale copy pins a browser to
                    // a build that no longer exists on disk.
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";

                    await context.Response.SendFileAsync(index, context.RequestAborted);
                });
            });
    }

    /// <summary>
    /// Whether this request is a browser asking for a page rather than for a file.
    ///
    /// "Names a file" is decided by the last segment having an extension, which is the same
    /// test the framework's own SPA fallback uses. It is a heuristic about URLs and not about
    /// the file system on purpose: a request for a bundle that is not there must fail as a
    /// missing bundle even when the directory it should have been in does not exist either.
    /// </summary>
    private static bool WantsAPage(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value;

        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return true;
        }

        var lastSlash = path.LastIndexOf('/');
        var last = lastSlash >= 0 ? path.AsSpan(lastSlash + 1) : path.AsSpan();

        return !last.Contains('.');
    }

    /// <summary>
    /// How long a browser may keep a file.
    ///
    /// Vite writes a content hash into every asset name, so a changed file is a different URL
    /// and the old one is never asked for again - which is exactly when a long immutable
    /// lifetime is safe. index.html is the exception and is handled where it is served.
    ///
    /// This is a local application. There is no CDN, no edge, and no revalidation strategy
    /// beyond these two cases.
    /// </summary>
    private static void Cache(StaticFileResponseContext context)
    {
        var name = context.File.Name;

        var hashed = name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                     || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                     || name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);

        context.Context.Response.GetTypedHeaders().CacheControl = hashed
            ? new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(365) }
            : new CacheControlHeaderValue { NoCache = true };
    }
}
