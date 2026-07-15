using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string PlanJson =
        """
        {
          "events": {
            "product_viewed":   { "origin": "client", "destinations": ["ga4", "amplitude"] },
            "checkout_started": { "origin": "client", "destinations": ["ga4"] },
            "order_placed":     { "origin": "server", "destinations": ["ga4", "amplitude", "moengage"] },
            "first_visit":      { "origin": "server", "destinations": ["moengage"] }
          }
        }
        """;

    private NpgsqlDataSource _ds = null!;
    private TrackingPlan _plan = null!;
    private RunningApi _api = null!;
    private HttpClient _pub = null!;
    private HttpClient _int = null!;

    public async Task InitializeAsync()
    {
        _ds = await pg.CreateMigratedDatabaseAsync();
        _plan = TrackingPlan.Parse(PlanJson);
        await RegistrySync.SyncAsync(_ds, _plan);
        _api = await ApiApp.StartAsync(Config(), _ds, _plan, new MetricsRegistry());
        _pub = NewClient(_api.PublicBaseUri, "tok-web");
        _int = NewClient(_api.InternalBaseUri, "internal-secret");
    }

    public async Task DisposeAsync()
    {
        _pub.Dispose();
        _int.Dispose();
        await _api.DisposeAsync();
    }

    private static EpConfig Config(int ratePermits = 1000) => new()
    {
        DbConnString = "unused-in-tests",
        Listen = "http://127.0.0.1:0",
        InternalListen = "http://127.0.0.1:0",
        ClientTokens = new() { ["tok-web"] = "webapp" },
        InternalToken = "internal-secret",
        CorsOrigins = ["https://shop.example"],
        RateLimitPermits = ratePermits,
        RateLimitWindowSeconds = 60,
    };

    private static HttpClient NewClient(Uri baseUri, string? bearer)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            BaseAddress = baseUri,
        };
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return client;
    }

    private static string Ev(string name, Guid? id = null, Guid? anon = null,
        string? occurredAt = null, string extraJson = "")
        => $"{{\"event_id\":\"{id ?? Guid.NewGuid()}\",\"event_name\":\"{name}\"," +
           $"\"occurred_at\":\"{occurredAt ?? DateTimeOffset.UtcNow.ToString("O")}\"," +
           $"\"anonymous_id\":\"{anon ?? Guid.NewGuid()}\"{extraJson}}}";

    private static StringContent Batch(params string[] events)
        => new($"{{\"events\":[{string.Join(',', events)}]}}", Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task Events_endpoint_requires_known_bearer_token()
    {
        using var anonymous = NewClient(_api.PublicBaseUri, null);
        using var wrong = NewClient(_api.PublicBaseUri, "tok-nope");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/v1/events", Batch(Ev("product_viewed")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrong.PostAsync("/v1/events", Batch(Ev("product_viewed")))).StatusCode);
    }

    [Fact]
    public async Task Token_accepted_via_query_param_for_sendBeacon()
    {
        // navigator.sendBeacon cannot set an Authorization header (SPEC §7)
        using var client = NewClient(_api.PublicBaseUri, null);
        var response = await client.PostAsync("/v1/events?token=tok-web", Batch(Ev("product_viewed")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var wrong = await client.PostAsync("/v1/events?token=nope", Batch(Ev("product_viewed")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    // ------------------------------------------------------- happy ingestion

    [Fact]
    public async Task Valid_client_event_is_stored_and_fanned_out_with_ip()
    {
        var eventId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = Batch(Ev("product_viewed", eventId,
                extraJson: ",\"properties\":{\"sku\":\"A1\"},\"context\":{\"page\":{\"path\":\"/p/a1\"},\"engagement_time_msec\":1200,\"session_number\":3,\"sdk\":{\"name\":\"event-pump-web\",\"version\":\"0.1.0\"},\"junk_key\":true}")),
        };
        request.Headers.Add("X-Real-IP", "203.0.113.9");

        var response = await _pub.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await Json(response);
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("rejected").EnumerateArray());

        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{eventId}' AND origin = 'client'"));
        Assert.Equal("A1", await Db.Scalar<string>(_ds,
            $"SELECT properties->>'sku' FROM events_outbox WHERE event_id = '{eventId}'"));
        Assert.Equal("203.0.113.9", await Db.Scalar<string>(_ds,
            $"SELECT context->>'ip' FROM events_outbox WHERE event_id = '{eventId}'"));
        // unknown context keys silently dropped (SPEC §1)
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{eventId}' AND context ? 'junk_key'"));
        Assert.Equal(["amplitude", "ga4"], await Db.DeliveryDestinations(_ds, eventId));
    }

    // --------------------------------------------------------- validation

    [Fact]
    public async Task Invalid_events_are_rejected_per_event_not_per_batch()
    {
        var okId = Guid.NewGuid();
        var response = await _pub.PostAsync("/v1/events",
            Batch(Ev("product_viewed", okId), Ev("never_registered")));

        using var body = await Json(response);
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        var rejected = body.RootElement.GetProperty("rejected").EnumerateArray().Single();
        Assert.Equal(1, rejected.GetProperty("index").GetInt32());
        Assert.Equal("unknown_event_name", rejected.GetProperty("reason").GetString());
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{okId}'"));
    }

    [Fact]
    public async Task Server_origin_names_are_rejected_on_client_endpoint()
    {
        var response = await _pub.PostAsync("/v1/events", Batch(Ev("order_placed")));
        using var body = await Json(response);
        Assert.Equal(0, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal("unknown_event_name",
            body.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Unknown_top_level_field_is_rejected()
    {
        var response = await _pub.PostAsync("/v1/events",
            Batch(Ev("product_viewed", extraJson: ",\"surprise\":1")));
        using var body = await Json(response);
        Assert.Equal("unknown_field:surprise",
            body.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Oversize_event_is_rejected()
    {
        var big = new string('x', 33_000);
        var response = await _pub.PostAsync("/v1/events",
            Batch(Ev("product_viewed", extraJson: $",\"properties\":{{\"blob\":\"{big}\"}}")));
        using var body = await Json(response);
        Assert.Equal("event_too_large",
            body.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Occurred_at_outside_window_is_rejected()
    {
        var stale = DateTimeOffset.UtcNow.AddDays(-8).ToString("O");
        var nearFuture = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O");

        var response = await _pub.PostAsync("/v1/events",
            Batch(Ev("product_viewed", occurredAt: stale),
                  Ev("product_viewed", occurredAt: nearFuture)));

        using var body = await Json(response);
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        var rejected = body.RootElement.GetProperty("rejected").EnumerateArray().Single();
        Assert.Equal(0, rejected.GetProperty("index").GetInt32());
        Assert.Equal("occurred_at_out_of_range", rejected.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Batch_over_100_events_is_rejected_whole()
    {
        var events = Enumerable.Range(0, 101).Select(_ => Ev("product_viewed")).ToArray();
        var response = await _pub.PostAsync("/v1/events", Batch(events));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds, "SELECT count(*) FROM events_outbox"));
    }

    [Fact]
    public async Task Duplicate_event_id_is_idempotent_accept()
    {
        var id = Guid.NewGuid();
        var first = await _pub.PostAsync("/v1/events", Batch(Ev("product_viewed", id)));
        var second = await _pub.PostAsync("/v1/events", Batch(Ev("product_viewed", id)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var body = await Json(second);
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{id}'"));
    }

    // ------------------------------------------------------------- cookie

    [Fact]
    public async Task Ep_aid_cookie_is_set_only_when_absent()
    {
        var anon = Guid.NewGuid();
        var bare = await _pub.PostAsync("/v1/events", Batch(Ev("product_viewed", anon: anon)));

        var setCookie = Assert.Single(bare.Headers.GetValues("Set-Cookie"));
        Assert.Contains($"ep_aid={anon}", setCookie);
        Assert.Contains("max-age=34128000", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        var withCookie = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = Batch(Ev("product_viewed", anon: anon)),
        };
        withCookie.Headers.Add("Cookie", $"ep_aid={anon}");
        var repeat = await _pub.SendAsync(withCookie);
        Assert.False(repeat.Headers.Contains("Set-Cookie"));
    }

    // ------------------------------------------------------------ identity

    [Fact]
    public async Task Identity_upsert_is_partial_and_merges()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();

        var full = new HttpRequestMessage(HttpMethod.Post, "/v1/identity")
        {
            Content = new StringContent(
                $"{{\"session_key\":\"{session}\",\"anonymous_id\":\"{anon}\"," +
                "\"session_number\":4,\"user_id\":\"u-77\"," +
                "\"handles\":{\"ga4_client_id\":\"123.456\",\"click_ids\":{\"gclid\":{\"value\":\"g1\",\"captured_at\":\"2026-07-13T00:00:00Z\"}}}," +
                "\"context\":{\"language\":\"ar\",\"timezone\":\"Asia/Baghdad\"}}",
                Encoding.UTF8, "application/json"),
        };
        full.Headers.Add("X-Real-IP", "203.0.113.7");
        Assert.Equal(HttpStatusCode.NoContent, (await _pub.SendAsync(full)).StatusCode);

        var partial = await _pub.PostAsync("/v1/identity", new StringContent(
            $"{{\"session_key\":\"{session}\",\"anonymous_id\":\"{anon}\"," +
            "\"handles\":{\"adjust_adid\":\"adid-9\",\"click_ids\":{\"fbclid\":{\"value\":\"f1\",\"captured_at\":\"2026-07-13T00:05:00Z\"}}}," +
            "\"context\":{\"model\":\"Pixel 9\"}}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, partial.StatusCode);

        Assert.Equal(1L, await Db.Scalar<long>(_ds, "SELECT count(*) FROM identity_registry"));
        Assert.Equal("u-77", await Db.Scalar<string>(_ds,
            $"SELECT user_id FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("123.456", await Db.Scalar<string>(_ds,
            $"SELECT ga4_client_id FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("adid-9", await Db.Scalar<string>(_ds,
            $"SELECT adjust_adid FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("203.0.113.7", await Db.Scalar<string>(_ds,
            $"SELECT client_ip FROM identity_registry WHERE session_key = '{session}'"));
        // click_ids merged across calls, context merged at top level
        Assert.Equal("g1", await Db.Scalar<string>(_ds,
            $"SELECT click_ids->'gclid'->>'value' FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("f1", await Db.Scalar<string>(_ds,
            $"SELECT click_ids->'fbclid'->>'value' FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("ar", await Db.Scalar<string>(_ds,
            $"SELECT context->>'language' FROM identity_registry WHERE session_key = '{session}'"));
        Assert.Equal("Pixel 9", await Db.Scalar<string>(_ds,
            $"SELECT context->>'model' FROM identity_registry WHERE session_key = '{session}'"));
    }

    [Fact]
    public async Task Identity_emits_first_visit_once_ever()
    {
        var anon = Guid.NewGuid();
        foreach (var session in (Guid[])[Guid.NewGuid(), Guid.NewGuid()])
        {
            var response = await _pub.PostAsync("/v1/identity", new StringContent(
                $"{{\"session_key\":\"{session}\",\"anonymous_id\":\"{anon}\",\"session_number\":1}}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM first_seen WHERE anonymous_id = '{anon}'"));
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_name = 'first_visit' AND anonymous_id = '{anon}'"));
        var firstVisitId = await Db.Scalar<Guid>(_ds,
            $"SELECT event_id FROM events_outbox WHERE event_name = 'first_visit' AND anonymous_id = '{anon}'");
        Assert.Equal(["moengage"], await Db.DeliveryDestinations(_ds, firstVisitId));
    }

    // ------------------------------------------------- internal listener

    [Fact]
    public async Task Internal_endpoint_lives_only_on_internal_listener()
    {
        // wrong port
        Assert.Equal(HttpStatusCode.NotFound,
            (await _pub.PostAsync("/internal/v1/events", Batch(Ev("order_placed")))).StatusCode);
        // client endpoint absent on internal port
        Assert.Equal(HttpStatusCode.NotFound,
            (await _int.PostAsync("/v1/events", Batch(Ev("product_viewed")))).StatusCode);
        // client token is not internal auth
        using var wrongAuth = NewClient(_api.InternalBaseUri, "tok-web");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrongAuth.PostAsync("/internal/v1/events", Batch(Ev("order_placed")))).StatusCode);

        // happy path: origin=server enforced
        var okId = Guid.NewGuid();
        var response = await _int.PostAsync("/internal/v1/events",
            Batch(Ev("order_placed", okId), Ev("product_viewed")));
        using var body = await Json(response);
        Assert.Equal(1, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal("unknown_event_name",
            body.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString());
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{okId}' AND origin = 'server'"));
    }

    // ------------------------------------------------------ infrastructure

    [Fact]
    public async Task Internal_events_endpoint_rejects_reserved_event_names()
    {
        // SPEC §6.1: `ep_attributes_synced` is auto-registered as reserved by
        // TrackingPlan.Parse; producers cannot emit it via HTTP or SQL.
        var response = await _int.PostAsync("/internal/v1/events", Batch(Ev("ep_attributes_synced")));
        using var body = await Json(response);
        Assert.Equal(0, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal("reserved_event_name",
            body.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Healthz_reports_ok()
    {
        var response = await NewClient(_api.PublicBaseUri, null).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_exposed_on_internal_listener_only()
    {
        await _pub.PostAsync("/v1/events", Batch(Ev("product_viewed")));

        var metrics = await _int.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Contains("events_ingested_total{origin=\"client\",endpoint=\"/v1/events\"}",
            await metrics.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await _pub.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after()
    {
        await using var limited = await ApiApp.StartAsync(Config(ratePermits: 2), _ds, _plan, new MetricsRegistry());
        using var client = NewClient(limited.PublicBaseUri, "tok-web");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/events", Batch(Ev("product_viewed")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/v1/events", Batch(Ev("product_viewed")))).StatusCode);
        var third = await client.PostAsync("/v1/events", Batch(Ev("product_viewed")));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
    }

    [Fact]
    public async Task Cors_preflight_allows_only_configured_origins()
    {
        var allowed = new HttpRequestMessage(HttpMethod.Options, "/v1/events");
        allowed.Headers.Add("Origin", "https://shop.example");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        var allowedResponse = await _pub.SendAsync(allowed);
        Assert.Equal("https://shop.example",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var denied = new HttpRequestMessage(HttpMethod.Options, "/v1/events");
        denied.Headers.Add("Origin", "https://evil.example");
        denied.Headers.Add("Access-Control-Request-Method", "POST");
        var deniedResponse = await _pub.SendAsync(denied);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
