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

/// <summary>Builds and starts the ingestion API (SPEC v1.2 §9) on two listeners.</summary>
public static class ApiApp
{
    public static async Task<RunningApi> StartAsync(
        EpConfig config, NpgsqlDataSource dataSource, TenantRegistry tenants, MetricsRegistry metrics)
    {
        var ingested = metrics.Counter(
            "events_ingested_total", "Events accepted at ingestion.",
            "app_id", "origin", "endpoint");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024; // SPEC §9: 413 beyond
            kestrel.Listen(ParseBind(config.Listen));
            kestrel.Listen(ParseBind(config.InternalListen));
        });

        // SPEC §9.5: CORS is per-tenant. On a shared listener we union the
        // allowed origins across every tenant and let the browser's Origin
        // check + our token check together isolate a request to its tenant.
        var allOrigins = tenants.All
            .SelectMany(t => t.CorsOrigins)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (allOrigins.Length > 0)
        {
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
                .WithOrigins(allOrigins)
                .WithMethods("POST")
                .WithHeaders("Authorization", "Content-Type")
                .AllowCredentials()));
        }

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var seconds = ResolveClientTenant(context.HttpContext, tenants)?.RateLimitWindowSeconds
                              ?? 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString();
                return ValueTask.CompletedTask;
            };
            options.AddPolicy("client", context =>
            {
                var tenant = ResolveClientTenant(context, tenants);
                var token = BearerToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                // Partition by (app_id, token) so one tenant cannot starve
                // another's bucket, and inside a tenant each token still gets
                // its own counter.
                var appId = tenant?.AppId ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{appId}:{token}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = tenant?.RateLimitPermits ?? 600,
                        Window = TimeSpan.FromSeconds(tenant?.RateLimitWindowSeconds ?? 60),
                        QueueLimit = 0,
                    });
            });
            options.AddPolicy("errors", context =>
            {
                var tenant = ResolveClientTenant(context, tenants);
                var token = BearerToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var appId = tenant?.AppId ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"err:{appId}:{token}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = tenant?.ErrorRateLimitPermits ?? 120,
                        Window = TimeSpan.FromSeconds(tenant?.ErrorRateLimitWindowSeconds ?? 60),
                        QueueLimit = 0,
                    });
            });
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
            if (path.StartsWithSegments("/docs"))
            {
                if (config.Docs == "both" || onInternalListener) await next();
                else context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (path.StartsWithSegments("/healthz") || onInternalListener == internalOnlyPath)
            {
                await next();
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        if (allOrigins.Length > 0) app.UseCors();
        app.UseRateLimiter();

        app.MapPost("/v1/events", (RequestDelegate)(async context =>
        {
            if (ResolveClientTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IngestAsync(context, tenant, "client", "/v1/events");
        })).RequireRateLimiting("client");

        app.MapPost("/v1/identity", (RequestDelegate)(async context =>
        {
            if (ResolveClientTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IdentityAsync(context, tenant);
        })).RequireRateLimiting("client");

        app.MapPost("/v1/errors", (RequestDelegate)(async context =>
        {
            if (ResolveClientTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await ErrorReports.HandleAsync(context, dataSource, tenant.AppId);
        })).RequireRateLimiting("errors");

        app.MapGet("/internal/v1/query/events", (RequestDelegate)(context =>
            QueryApi.EventsAsync(context, dataSource, config)));

        app.MapGet("/internal/v1/query/identity/{sessionKey}", (RequestDelegate)(context =>
            QueryApi.IdentityAsync(context, dataSource)));

        // POST /internal/v1/events. The internal token identifies the tenant
        // whose plan governs validation and whose app_id is written to storage.
        app.MapPost("/internal/v1/events", (RequestDelegate)(async context =>
        {
            if (ResolveInternalTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await IngestAsync(context, tenant, "server", "/internal/v1/events");
        }));

        // SPEC §9.6: DSR deletion of the person-scoped user_attributes row for
        // a specific tenant. URL carries {app_id} so an operator with one
        // tenant's internal token can not touch another tenant's users.
        app.MapDelete("/internal/v1/user_attributes/{appId}/{userId}", (RequestDelegate)(async context =>
        {
            if (ResolveInternalTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            var appId = (string?)context.Request.RouteValues["appId"];
            var userId = (string?)context.Request.RouteValues["userId"];
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(userId))
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "missing_user_id");
                return;
            }
            if (appId != tenant.AppId)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await EventStore.DeleteUserAttributesAsync(dataSource, tenant.AppId, userId, context.RequestAborted);
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

        if (config.Docs != "off") OpenApiDocs.MapEndpoints(app);

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        var publicUri = new Uri(addresses[0]);
        var internalUri = new Uri(addresses[1]);
        internalPort.Port = internalUri.Port;

        return new RunningApi { PublicBaseUri = publicUri, InternalBaseUri = internalUri, App = app };

        // ------------------------------------------------------------ ingest

        async Task IngestAsync(HttpContext context, TenantConfig tenant, string origin, string endpoint)
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
                    events, origin, tenant.Plan, clientIp, DateTimeOffset.UtcNow);

                await EventStore.InsertBatchAsync(dataSource, tenant.AppId, origin, valid, context.RequestAborted);

                if (origin == "client" && valid.Count > 0 && valid[0].AnonymousId is { } anonymousId)
                    MaybeSetAidCookie(context, anonymousId, tenant);

                if (valid.Count > 0) ingested.WithLabels(tenant.AppId, origin, endpoint).Inc(valid.Count);

                await context.Response.WriteAsJsonAsync(
                    new EventsResponse(valid.Count, rejected), ApiJsonContext.Default.EventsResponse);
            }
        }

        // ---------------------------------------------------------- identity

        async Task IdentityAsync(HttpContext context, TenantConfig tenant)
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
                var (identity, attributes, error) = IdentityValidation.Parse(document.RootElement, tenant.Plan);
                if (identity is null)
                {
                    await WriteError(context, StatusCodes.Status400BadRequest, "invalid_identity", error);
                    return;
                }

                // SPEC §6.1: attributes need a user_id — from this body or the
                // session's registry row. Resolved BEFORE the identity write so
                // every attribute rejection behaves the same way: 400 with
                // nothing persisted. Scoped to the tenant's app_id so one
                // tenant's SDK cannot resolve another tenant's session.
                var pendingAttributes = attributes is { IsEmpty: false } ? attributes : null;
                string? attributesUserId = null;
                if (pendingAttributes is not null)
                {
                    attributesUserId = identity.UserId
                        ?? await EventStore.LookupUserIdBySessionAsync(
                            dataSource, tenant.AppId, identity.SessionKey, context.RequestAborted);
                    if (attributesUserId is null)
                    {
                        await WriteError(context, StatusCodes.Status400BadRequest, "attributes_require_user_id");
                        return;
                    }
                }

                await EventStore.UpsertIdentityAsync(
                    dataSource, tenant.AppId, identity, RealIp(context), context.RequestAborted);

                if (pendingAttributes is not null && attributesUserId is { } userId)
                {
                    var result = await EventStore.UpsertUserAttributesAsync(
                        dataSource, tenant.AppId, userId, pendingAttributes.AttributesJson, context.RequestAborted);

                    // SPEC §6.1: enqueue moengage_customer only when this tenant
                    // has MoEngage enabled AND its attribute gate on AND the
                    // merged state actually changed since the last successful sync.
                    if (tenant.MoEngageEnabled
                        && tenant.MoEngageAttributesEnabled
                        && result.MergedJson != "{}"
                        && result.NewHash != result.PreviousSyncedHash)
                    {
                        await EventStore.EnqueueAttributesSyncAsync(
                            dataSource, tenant.AppId, userId, context.RequestAborted);
                    }
                }

                MaybeSetAidCookie(context, identity.AnonymousId, tenant);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        }
    }

    // ------------------------------------------------------------- helpers

    private static TenantConfig? ResolveClientTenant(HttpContext context, TenantRegistry tenants)
    {
        if (BearerToken(context) is not { } token) return null;
        if (tenants.ByClientToken(token) is not { } tenant) return null;
        // SPEC §9.1: SDK also sends X-App-Id as defense in depth. One
        // tenant may have several client faces (mobile pubspec name,
        // web domain, mobile web domain, ...); each declared value must
        // appear in tenant.ClientAppIds. A leaked token misfired by a
        // different app face fails here even though the token is valid.
        // Missing header = 401 (SDK is required to set it since v1.2).
        // The internal path does not consult X-App-Id — server producers
        // speak for whichever tenant their internal token belongs to.
        // sendBeacon can't set headers (SPEC §7); fall back to ?app_id=
        // query param, matching how BearerToken accepts ?token=.
        string? claimedAppId = context.Request.Headers["X-App-Id"];
        if (string.IsNullOrEmpty(claimedAppId)
            && context.Request.Query["app_id"] is [{ Length: > 0 } q, ..]) claimedAppId = q;
        if (string.IsNullOrEmpty(claimedAppId)) return null;
        // Empty list = "accept only the canonical app_id"; explicit list wins.
        var accepted = tenant.ClientAppIds.Length == 0 ? [tenant.AppId] : tenant.ClientAppIds;
        foreach (var candidate in accepted)
            if (FixedTimeEquals(claimedAppId, candidate)) return tenant;
        return null;
    }

    private static TenantConfig? ResolveInternalTenant(HttpContext context, TenantRegistry tenants)
    {
        if (BearerToken(context) is not { } token) return null;
        // constant-time comparison: iterate all tenants regardless of match
        TenantConfig? matched = null;
        foreach (var tenant in tenants.All)
        {
            if (tenant.InternalToken.Length == 0) continue;
            if (FixedTimeEquals(token, tenant.InternalToken)) matched = tenant;
        }
        return matched;
    }

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

    /// <summary>SPEC §9.5: the server (never the SDK) sets the ep_aid cookie; Domain per tenant.</summary>
    private static void MaybeSetAidCookie(HttpContext context, Guid anonymousId, TenantConfig tenant)
    {
        if (context.Request.Cookies.ContainsKey("ep_aid")) return;
        context.Response.Cookies.Append("ep_aid", anonymousId.ToString(), new CookieOptions
        {
            MaxAge = TimeSpan.FromSeconds(34_128_000), // ~13 months
            Path = "/",
            Domain = tenant.CookieDomain,
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
