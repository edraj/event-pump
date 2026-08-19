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

        // SPEC §9.5: CORS is per-tenant, but the CORS middleware runs before
        // any tenant is known — a preflight OPTIONS carries no Authorization
        // header at all — so the browser-facing policy can only be the union
        // of every tenant's origins. That union is deliberately NOT the
        // isolation boundary: it would let tenant B's origin call tenant A's
        // endpoint with credentials. OriginAllowed() below re-checks the
        // Origin against the *resolved* tenant on the real request and 403s a
        // mismatch, which is where the per-tenant boundary is actually held.
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
                // /v1/errors has its own bucket with its own window, so the
                // Retry-After must come from that window — not the events one.
                var tenant = ResolveClientTenant(context.HttpContext, tenants);
                var seconds = context.HttpContext.Request.Path.StartsWithSegments("/v1/errors")
                    ? tenant?.ErrorRateLimitWindowSeconds ?? 60
                    : tenant?.RateLimitWindowSeconds ?? 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString();
                return ValueTask.CompletedTask;
            };
            options.AddPolicy("client", context =>
            {
                var tenant = ResolveClientTenant(context, tenants);
                var token = ClientToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
                var token = ClientToken(context) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
            if (await AuthorizeClientAsync(context) is not { } tenant) return;
            await IngestAsync(context, tenant, "client", "/v1/events");
        })).RequireRateLimiting("client");

        app.MapPost("/v1/identity", (RequestDelegate)(async context =>
        {
            if (await AuthorizeClientAsync(context) is not { } tenant) return;
            await IdentityAsync(context, tenant);
        })).RequireRateLimiting("client");

        app.MapPost("/v1/errors", (RequestDelegate)(async context =>
        {
            if (await AuthorizeClientAsync(context) is not { } tenant) return;
            await ErrorReports.HandleAsync(context, dataSource, tenant.AppId);
        })).RequireRateLimiting("errors");

        app.MapGet("/internal/v1/query/events", (RequestDelegate)(async context =>
        {
            if (ResolveInternalTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await QueryApi.EventsAsync(context, dataSource, config, tenant);
        }));

        app.MapGet("/internal/v1/query/identity/{sessionKey}", (RequestDelegate)(async context =>
        {
            if (ResolveInternalTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return;
            }
            await QueryApi.IdentityAsync(context, dataSource, tenant);
        }));

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

        // ------------------------------------------------------------ auth

        // Client-listener gate: the tenant_api_key names the tenant, then the
        // browser's Origin must belong to *that* tenant (SPEC §9.5). The CORS
        // middleware alone cannot do the second half — see the union note at
        // the top of StartAsync.
        async Task<TenantConfig?> AuthorizeClientAsync(HttpContext context)
        {
            if (ResolveClientTenant(context, tenants) is not { } tenant)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized");
                return null;
            }
            if (!OriginAllowed(context, tenant))
            {
                await WriteError(context, StatusCodes.Status403Forbidden, "origin_not_allowed");
                return null;
            }
            return tenant;
        }

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
                    events, origin, tenant.Plan, clientIp, UserAgent(context), DateTimeOffset.UtcNow);

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
                    dataSource, tenant.AppId,
                    identity with
                    {
                        ContextJson = IdentityValidation.WithObservedUserAgent(
                            identity.ContextJson, UserAgent(context)),
                    },
                    RealIp(context), context.RequestAborted);

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
                        // Pass through the MoEngage-specific customer id and
                        // stash it on the outbox row — the reserved event has
                        // no session_key so the customer sender cannot reach
                        // identity_registry at delivery time. Resolution order:
                        //   1. handles in THIS /v1/identity call (fresh value)
                        //   2. previously-stored handle for this session
                        // Without step 2, a setUserAttributes call that omits
                        // handles (the common shape) would stash NULL and the
                        // sender would fall back to user_id — creating the
                        // two-profile split the handle is meant to prevent
                        // (PR #8 review open-question #6).
                        var moengageCustomerId = identity.MoEngageCustomerId
                            ?? await EventStore.LookupMoEngageCustomerIdBySessionAsync(
                                dataSource, tenant.AppId, identity.SessionKey, context.RequestAborted);
                        await EventStore.EnqueueAttributesSyncAsync(
                            dataSource, tenant.AppId, userId,
                            moengageCustomerId, context.RequestAborted);
                    }
                }

                MaybeSetAidCookie(context, identity.AnonymousId, tenant);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// SPEC v1.2 §9.1: match a bearer against every tenant's client
    /// `tenant_api_key`. Used only by the public listener (POST /v1/*).
    /// Constant-time compare across every tenant — never short-circuit on
    /// the first miss.
    /// </summary>
    private static TenantConfig? ResolveClientTenant(HttpContext context, TenantRegistry tenants)
    {
        if (ClientToken(context) is not { } token) return null;
        TenantConfig? matched = null;
        foreach (var t in tenants.All)
            if (t.TenantApiKey.Length > 0 && FixedTimeEquals(token, t.TenantApiKey)) matched = t;
        return matched;
    }

    /// <summary>
    /// SPEC §9.5: the CORS middleware can only police the union of every
    /// tenant's origins (a preflight carries no credential), so the real
    /// per-tenant check happens here, once the token has named a tenant. A
    /// browser request whose Origin belongs to a different tenant is refused.
    /// Non-browser callers (the mobile SDK, server producers) send no Origin
    /// and are unaffected; a tenant that declares no origins opts out.
    /// </summary>
    private static bool OriginAllowed(HttpContext context, TenantConfig tenant)
    {
        if (tenant.CorsOrigins.Length == 0) return true;
        if (context.Request.Headers.Origin is not [{ Length: > 0 } origin, ..]) return true;
        foreach (var allowed in tenant.CorsOrigins)
            if (StringComparer.OrdinalIgnoreCase.Equals(allowed, origin)) return true;
        return false;
    }

    /// <summary>
    /// SPEC v1.2 §9.3: match a bearer against every tenant's server
    /// `internal_token`. Used only by the internal listener (POST
    /// /internal/v1/events and DSR DELETE). A leaked client key does NOT
    /// resolve here — the two-tier trust model is enforced by having a
    /// separate secret backing this resolver.
    /// </summary>
    private static TenantConfig? ResolveInternalTenant(HttpContext context, TenantRegistry tenants)
    {
        if (BearerToken(context) is not { } token) return null;
        TenantConfig? matched = null;
        foreach (var t in tenants.All)
            if (t.InternalToken.Length > 0 && FixedTimeEquals(token, t.InternalToken)) matched = t;
        return matched;
    }

    /// <summary>
    /// The `Authorization: Bearer …` header, and nothing else. Every route
    /// accepts this form; it is the only form /internal/v1/* accepts.
    /// </summary>
    private static string? BearerToken(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;
        return header?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? header[7..] : null;
    }

    /// <summary>
    /// Client credential for /v1/*: the bearer header, or — because sendBeacon
    /// cannot set headers (SPEC §7) — a `?tenant_api_key=` query param on the
    /// page-unload flush. Query-string credentials leak into access logs,
    /// Referer headers and proxy caches, so this fallback exists only for the
    /// client key. ResolveInternalTenant() deliberately does not use it: the
    /// server-side internal_token must never travel in a URL.
    /// </summary>
    private static string? ClientToken(HttpContext context)
        => BearerToken(context)
           ?? (context.Request.Query["tenant_api_key"] is [{ Length: > 0 } fromQuery, ..] ? fromQuery : null);

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string? RealIp(HttpContext context)
        => context.Request.Headers["X-Real-IP"] is [{ } raw, ..]
           && IPAddress.TryParse(raw, out var ip)
            ? ip.ToString()
            : null;

    private static string? UserAgent(HttpContext context)
        => context.Request.Headers.UserAgent is [{ Length: > 0 } raw, ..]
            ? raw[..Math.Min(raw.Length, MaxUserAgentLength)]
            : null;

    private const int MaxUserAgentLength = 512;

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
