using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using EventPump.Config;
using EventPump.Data;
using EventPump.Model;
using EventPump.Observability;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace EventPump.Api;

/// <summary>Builds and starts the ingestion API (SPEC §9) on two listeners.</summary>
public static class ApiApp
{
    public static async Task<RunningApi> StartAsync(
        EpConfig config, NpgsqlDataSource dataSource, TrackingPlan plan, MetricsRegistry metrics)
    {
        var ingested = metrics.Counter(
            "events_ingested_total", "Events accepted at ingestion.", "origin", "endpoint");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024; // SPEC §9: 413 beyond
            kestrel.Listen(ParseBind(config.Listen));
            kestrel.Listen(ParseBind(config.InternalListen));
        });

        if (config.CorsOrigins.Length > 0)
        {
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(config.CorsOrigins)
                .WithMethods("POST")
                .WithHeaders("Authorization", "Content-Type")
                .AllowCredentials()));
        }

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    config.RateLimitWindowSeconds.ToString();
                return ValueTask.CompletedTask;
            };
            options.AddPolicy("client", context => RateLimitPartition.GetFixedWindowLimiter(
                BearerToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.RateLimitPermits,
                    Window = TimeSpan.FromSeconds(config.RateLimitWindowSeconds),
                    QueueLimit = 0,
                }));
            // separate bucket: an error storm must never throttle product events
            options.AddPolicy("errors", context => RateLimitPartition.GetFixedWindowLimiter(
                "err:" + (BearerToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.ErrorRateLimitPermits,
                    Window = TimeSpan.FromSeconds(config.ErrorRateLimitWindowSeconds),
                    QueueLimit = 0,
                }));
        });

        var app = builder.Build();
        var internalPort = new PortHolder();

        // Listener gate (SPEC §9.3): /internal/* and /metrics exist only on the
        // internal listener; /v1/* only on the public one; /healthz on both.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var onInternalListener = context.Connection.LocalPort == internalPort.Port;
            var internalOnlyPath = path.StartsWithSegments("/internal") || path.StartsWithSegments("/metrics");
            if (path.StartsWithSegments("/healthz") || onInternalListener == internalOnlyPath)
            {
                await next();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        if (config.CorsOrigins.Length > 0) app.UseCors();
        app.UseRateLimiter();

        app.MapPost("/v1/events", (RequestDelegate)(async context =>
        {
            if (!IsClientAuthorized(context, config))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IngestAsync(context, "client", "/v1/events");
        })).RequireRateLimiting("client");

        app.MapPost("/v1/identity", (RequestDelegate)(async context =>
        {
            if (!IsClientAuthorized(context, config))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IdentityAsync(context);
        })).RequireRateLimiting("client");

        app.MapPost("/v1/errors", (RequestDelegate)(async context =>
        {
            if (BearerToken(context) is not { } token
                || !config.ClientTokens.TryGetValue(token, out var appId))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await ErrorReports.HandleAsync(context, dataSource, appId);
        })).RequireRateLimiting("errors");

        app.MapGet("/internal/v1/query/events", (RequestDelegate)(context =>
            QueryApi.EventsAsync(context, dataSource, config)));

        app.MapGet("/internal/v1/query/identity/{sessionKey}", (RequestDelegate)(context =>
            QueryApi.IdentityAsync(context, dataSource)));

        app.MapPost("/internal/v1/events", (RequestDelegate)(async context =>
        {
            var token = BearerToken(context);
            if (token is null || config.InternalToken.Length == 0 || !FixedTimeEquals(token, config.InternalToken))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IngestAsync(context, "server", "/internal/v1/events");
        }));

        // SPEC §9.6: DSR deletion of the person-scoped user_attributes row.
        // DB-only in v1.1; downstream destination cleanup deferred to follow-up.
        app.MapDelete("/internal/v1/user_attributes/{userId}", (RequestDelegate)(async context =>
        {
            var token = BearerToken(context);
            if (token is null || config.InternalToken.Length == 0 || !FixedTimeEquals(token, config.InternalToken))
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            var userId = (string?)context.Request.RouteValues["userId"];
            if (string.IsNullOrEmpty(userId))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "missing_user_id");
                return;
            }
            await EventStore.DeleteUserAttributesAsync(dataSource, userId, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }));

        app.MapGet("/healthz", (RequestDelegate)(async context =>
        {
            try
            {
                await using var cmd = dataSource.CreateCommand("SELECT 1");
                await cmd.ExecuteScalarAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(
                    new HealthResponse("ok"), ApiJsonContext.Default.HealthResponse);
            }
            catch (Exception)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new HealthResponse("db_unreachable"), ApiJsonContext.Default.HealthResponse);
            }
        }));

        app.MapGet("/metrics", (RequestDelegate)(context =>
        {
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return context.Response.WriteAsync(metrics.Render());
        }));

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        var publicUri = new Uri(addresses[0]);
        var internalUri = new Uri(addresses[1]);
        internalPort.Port = internalUri.Port;

        return new RunningApi { PublicBaseUri = publicUri, InternalBaseUri = internalUri, App = app };

        // ------------------------------------------------------------ ingest

        async Task IngestAsync(HttpContext context, string origin, string endpoint)
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "malformed_json");
                return;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("events", out var events)
                    || events.ValueKind != JsonValueKind.Array)
                {
                    await WriteError(context, StatusCodes.Status400BadRequest, "missing_events_array");
                    return;
                }
                if (events.GetArrayLength() > EventValidation.MaxBatchSize)
                {
                    await WriteError(context, StatusCodes.Status400BadRequest, "batch_too_large",
                        $"max {EventValidation.MaxBatchSize} events per batch");
                    return;
                }

                var clientIp = origin == "client" ? RealIp(context) : null;
                var (valid, rejected) = EventValidation.ValidateBatch(
                    events, origin, plan, clientIp, DateTimeOffset.UtcNow);

                await EventStore.InsertBatchAsync(dataSource, origin, valid, context.RequestAborted);

                if (origin == "client" && valid.Count > 0 && valid[0].AnonymousId is { } anonymousId)
                    MaybeSetAidCookie(context, anonymousId, config);

                if (valid.Count > 0) ingested.WithLabels(origin, endpoint).Inc(valid.Count);

                await context.Response.WriteAsJsonAsync(
                    new EventsResponse(valid.Count, rejected), ApiJsonContext.Default.EventsResponse);
            }
        }

        // ---------------------------------------------------------- identity

        async Task IdentityAsync(HttpContext context)
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted);
            }
            catch (JsonException)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "malformed_json");
                return;
            }

            using (document)
            {
                var (identity, attributes, error) = IdentityValidation.Parse(document.RootElement, plan);
                if (identity is null)
                {
                    await WriteError(context, StatusCodes.Status400BadRequest, "invalid_identity", error);
                    return;
                }

                await EventStore.UpsertIdentityAsync(
                    dataSource, identity, RealIp(context), context.RequestAborted);

                if (attributes is not null && !attributes.IsEmpty)
                {
                    // SPEC §6.1: attributes need a user_id — from this body or the session's registry row.
                    var userId = identity.UserId
                        ?? await EventStore.LookupUserIdBySessionAsync(
                            dataSource, identity.SessionKey, context.RequestAborted);
                    if (userId is null)
                    {
                        await WriteError(context, StatusCodes.Status400BadRequest, "attributes_require_user_id");
                        return;
                    }
                    var result = await EventStore.UpsertUserAttributesAsync(
                        dataSource, userId, attributes.AttributesJson, context.RequestAborted);

                    // SPEC §6.1: when the current hash diverges from the last
                    // successfully-synced MoEngage hash and MoEngage attributes are
                    // enabled, enqueue a moengage_customer delivery so the sender
                    // can push a type:"customer" payload.
                    if (config.MoEngageEnabled
                        && config.MoEngageAttributesEnabled
                        && result.NewHash != result.PreviousSyncedHash)
                    {
                        await EventStore.EnqueueAttributesSyncAsync(
                            dataSource, userId, context.RequestAborted);
                    }
                }

                MaybeSetAidCookie(context, identity.AnonymousId, config);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        }
    }

    // ------------------------------------------------------------- helpers

    private static bool IsClientAuthorized(HttpContext context, EpConfig config)
        => BearerToken(context) is { } token && config.ClientTokens.ContainsKey(token);

    private static string? BearerToken(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;
        if (header?.StartsWith("Bearer ", StringComparison.Ordinal) == true) return header[7..];
        // sendBeacon cannot set headers (SPEC §7); tokens identify + rate-limit,
        // they are not secrets, so a query param is an acceptable carrier.
        return context.Request.Query["token"] is [{ Length: > 0 } fromQuery, ..] ? fromQuery : null;
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string? RealIp(HttpContext context)
        => context.Request.Headers["X-Real-IP"] is [{ } raw, ..]
           && IPAddress.TryParse(raw, out var ip)
            ? ip.ToString()
            : null;

    /// <summary>SPEC §9.5: the server (never the SDK) sets the ep_aid cookie.</summary>
    private static void MaybeSetAidCookie(HttpContext context, Guid anonymousId, EpConfig config)
    {
        if (context.Request.Cookies.ContainsKey("ep_aid")) return;
        context.Response.Cookies.Append("ep_aid", anonymousId.ToString(), new CookieOptions
        {
            MaxAge = TimeSpan.FromSeconds(34_128_000), // ~13 months
            Path = "/",
            Domain = config.CookieDomain,
            Secure = true,
            HttpOnly = false, // the SDK must read it
            SameSite = SameSiteMode.Lax,
        });
    }

    private static Task WriteError(HttpContext context, int status, string error, string? detail = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(error, detail), ApiJsonContext.Default.ErrorResponse);
    }

    internal static IPEndPoint ParseBind(string url)
    {
        var uri = new Uri(url);
        var host = uri.Host switch
        {
            "localhost" => IPAddress.Loopback,
            "*" or "+" => IPAddress.Any,
            var literal => IPAddress.Parse(literal),
        };
        return new IPEndPoint(host, uri.Port);
    }

    private sealed class PortHolder
    {
        public volatile int Port = -1;
    }
}

/// <summary>A started API with its resolved listener addresses.</summary>
public sealed class RunningApi : IAsyncDisposable
{
    public required Uri PublicBaseUri { get; init; }
    public required Uri InternalBaseUri { get; init; }
    public required WebApplication App { get; init; }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
