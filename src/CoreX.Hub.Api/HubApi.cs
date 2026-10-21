using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using CoreX.Hub.Composition;
using CoreX.Hub.Composition.Autopilot;

namespace CoreX.Hub.Api;

/// <summary>
/// The local operator API.
///
/// It is an adapter and nothing more. Every route below reaches a Phase 1-8 authority and
/// carries its answer outward; none of them decides whether a task may run, whether an
/// execution passed, or whether a person may accept one. That is the property the whole
/// phase rests on, and it is guarded rather than trusted.
///
/// The host binds a loopback address and has no authentication, in that order. A single-user
/// local tool that drives real agents against real repositories can defensibly skip identity
/// - but only while nothing off the machine can reach it, so the binding is the security
/// model and it is not a setting.
///
/// It also serves the built operator UI, on the same origin and the same port, when there is
/// one to serve. One origin is not a step towards remote hosting: the address this binds is
/// still loopback, and still cannot be configured to be anything else.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HubApi
{
    public const string RoutePrefix = "/api/v1";

    /// <summary>Cross-origin is refused unless a developer explicitly named an origin here.</summary>
    public const string DevelopmentCorsPolicy = "hub-local-development";

    public static WebApplication Build(HubApiOptions options, string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(args ?? []);

        // Loopback, always, and stated here rather than left to launch configuration - a
        // profile that is absent in production must not be the only thing keeping the Hub
        // off the network.
        builder.WebHost.UseUrls(options.Bind());

        builder.Services.AddSingleton(options);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            // A TypeScript client must never depend on an ordinal.
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

            // Nor on a local offset. Every instant crosses as UTC ending in Z.
            json.SerializerOptions.Converters.Add(new UtcInstantConverter());

            // Nulls are written rather than omitted: a field that is sometimes absent and
            // sometimes null makes a typed client handle two shapes for one fact.
        });

        builder.Services.AddSingleton(_ =>
            HubRuntime.StartAsync(options.DataDirectory, options.ConfigPath, TextWriter.Null, CancellationToken.None)
                .GetAwaiter()
                .GetResult());

        builder.Services.AddSingleton<ExecutionEventStream>();
        builder.Services.AddSingleton<ExecutionLauncher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ExecutionLauncher>());

        // ADR-0028. After the launcher, so it is stopped before the launcher drains.
        builder.Services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<HubRuntime>();

            return new AutopilotCoordinator(
                runtime,
                new LauncherRunStarter(sp.GetRequiredService<ExecutionLauncher>()),
                new SessionBranchLineage(runtime.DataDirectory, runtime.ConfigPath),
                TimeProvider.System);
        });
        builder.Services.AddHostedService<AutopilotHost>();

        if (options.DevelopmentCorsOrigins.Count > 0)
        {
            builder.Services.AddCors(cors => cors.AddPolicy(
                DevelopmentCorsPolicy,
                policy => policy
                    .WithOrigins([.. options.DevelopmentCorsOrigins])
                    .AllowAnyHeader()
                    .AllowAnyMethod()));
        }

        var app = builder.Build();

        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            // Nothing about the exception crosses the wire. What an operator gets is a
            // correlation id; what the log gets is everything.
            await context.Response.WriteAsJsonAsync(ApiErrors.Unexpected());
        }));

        // Named origins only, and only in Development. A production host serves same-origin
        // whatever configuration says, because the check is on the environment.
        if (options.DevelopmentCorsOrigins.Count > 0 && app.Environment.IsDevelopment())
        {
            app.UseCors(DevelopmentCorsPolicy);
        }

        // Before anything that could answer an API request, and it excludes API paths itself.
        // Both halves matter: the branch has to see a browser route before routing decides
        // nothing matches, and it must never be what an /api request meets first.
        if (options.ServeOperatorUi)
        {
            app.UseOperatorUi(options.WebRoot);
        }

        var api = app.MapGroup(RoutePrefix);

        CatalogEndpoints.Map(api);
        TaskEndpoints.Map(api);
        ExecutionEndpoints.Map(api);
        AcceptanceEndpoints.Map(api);
        DiagnosticsEndpoints.Map(api);
        AutopilotEndpoints.Map(api);

        return app;
    }
}

/// <summary>
/// Writes every instant as UTC ISO-8601 ending in Z.
///
/// <see cref="DateTimeOffset"/> serializes its offset by default, so a Hub running in
/// Istanbul would hand a browser "+03:00" and a Hub running in London "+00:00" for the same
/// moment. Both are correct and neither is a contract. This makes the wire format one thing.
/// </summary>
internal sealed class UtcInstantConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? string.Empty,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        DateTimeOffset value,
        System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture));
}
