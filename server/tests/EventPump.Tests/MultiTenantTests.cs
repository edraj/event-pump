using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EventPump.Api;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// SPEC v1.2 §11: two tenants sharing one database must never see each
/// other's rows. This suite exercises the isolation seams: bearer -> tenant
/// routing, event_registry composite key, user_attributes composite key,
/// DSR endpoint scoping, and worker claim narrowing.
///
/// Auth model: two per-tenant secrets. `tenant_api_key` (client) authenticates
/// SDK traffic on the public listener; `internal_token` (server) authenticates
/// backend producers + DSR on the internal listener. The two keys per tenant
/// must be distinct and never cross listeners.
/// </summary>
[Collection("pg")]
public class MultiTenantTests(PostgresFixture pg) : IAsyncLifetime
{
    private Npgsql.NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private TenantRegistry _tenants = null!;

    private const string AcmeClientKey     = "acme-client-key";
    private const string AcmeInternalKey   = "acme-internal-secret";
    private const string WidgetsClientKey  = "widgets-client-key";
    private const string WidgetsInternalKey = "widgets-internal-secret";

    private static TrackingPlan Plan() => TrackingPlan.Parse(
        """
        {
          "attributes": { "email": { "type": "email", "max_length": 254 } },
          "events": {
            "product_viewed": { "origin": "client", "destinations": [] },
            "order_placed":   { "origin": "server", "destinations": [] }
          }
        }
        """);

    public async Task InitializeAsync()
    {
        _ds = await pg.CreateMigratedDatabaseAsync();
        _tenants = TenantRegistry.ForTesting(
            new TenantConfig
            {
                AppId = "acme",
                TenantApiKey = AcmeClientKey,
                InternalToken = AcmeInternalKey,
                CookieDomain = ".acme.example",
                CorsOrigins = ["https://www.acme.example"],
                Plan = Plan(),
            },
            new TenantConfig
            {
                AppId = "widgets",
                TenantApiKey = WidgetsClientKey,
                InternalToken = WidgetsInternalKey,
                CookieDomain = ".widgets.example",
                CorsOrigins = ["https://www.widgets.example"],
                Plan = Plan(),
            });
        await RegistrySync.SyncAllAsync(_ds, _tenants);
        _api = await ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
        }, _ds, _tenants, new MetricsRegistry());
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        // Release the pool now rather than at fixture teardown: every test gets
        // its own database, and holding all of them open at once outruns
        // Postgres's max_connections long before the suite finishes.
        await _ds.DisposeAsync();
    }

    private HttpClient PublicClient(string bearer) =>
        Client(_api.PublicBaseUri, bearer);

    private HttpClient InternalClient(string bearer) =>
        Client(_api.InternalBaseUri, bearer);

    private static HttpClient Client(Uri baseUri, string bearer)
    {
        var c = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = baseUri };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private static StringContent Body(string bodyJson) => new(bodyJson, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Two_tenants_ingest_into_separate_app_id_rows()
    {
        using var acme = PublicClient(AcmeClientKey);
        using var widgets = PublicClient(WidgetsClientKey);

        Assert.Equal(HttpStatusCode.OK, (await acme.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await widgets.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""))).StatusCode);

        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'acme'"));
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'widgets'"));
    }

    [Fact]
    public async Task Unknown_bearer_is_rejected_with_401()
    {
        using var stranger = PublicClient("nobody-knows-me");
        var response = await stranger.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Event_name_registered_for_one_tenant_is_rejected_for_the_other()
    {
        // Widgets never registers "widget_only"; acme does.
        await Db.RegisterEventForApp(_ds, "acme", "widget_only", "client");
        // From widgets' side, /v1/events must reject the name — the validator
        // reads the widgets plan, and "widget_only" is not in it.
        using var widgets = PublicClient(WidgetsClientKey);
        var response = await widgets.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"widget_only","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"accepted\":0", body);
        Assert.Contains("unknown_event_name", body);
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'widgets' AND event_name = 'widget_only'"));
    }

    [Fact]
    public async Task User_attributes_isolated_by_app_id()
    {
        // Same user_id "shared-42" exists in both tenants and stores different data.
        await EventStore.UpsertUserAttributesAsync(_ds, "acme", "shared-42",
            """{"email":"ali@acme.example"}""", default);
        await EventStore.UpsertUserAttributesAsync(_ds, "widgets", "shared-42",
            """{"email":"ali@widgets.example"}""", default);

        Assert.Equal("ali@acme.example", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'email' FROM user_attributes WHERE app_id = 'acme' AND user_id = 'shared-42'"));
        Assert.Equal("ali@widgets.example", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'email' FROM user_attributes WHERE app_id = 'widgets' AND user_id = 'shared-42'"));
    }

    [Fact]
    public async Task Dsr_endpoint_rejects_cross_tenant_internal_token_use()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "acme", "u-secret",
            """{"email":"secret@acme.example"}""", default);

        // widgets' internal token cannot DSR-delete an acme user (URL is scoped)
        using var widgets = InternalClient(WidgetsInternalKey);
        var response = await widgets.DeleteAsync("/internal/v1/user_attributes/acme/u-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Row still there
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE app_id = 'acme' AND user_id = 'u-secret'"));

        // acme's internal token succeeds
        using var acme = InternalClient(AcmeInternalKey);
        Assert.Equal(HttpStatusCode.NoContent,
            (await acme.DeleteAsync("/internal/v1/user_attributes/acme/u-secret")).StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE app_id = 'acme' AND user_id = 'u-secret'"));
    }

    [Fact]
    public async Task Client_api_key_is_rejected_on_the_internal_listener()
    {
        // Two-tier trust model: the SDK-side key must not authenticate anything
        // on /internal/v1/*. If this fails, a leaked mobile bundle can DSR-erase
        // users — the whole point of the split.
        using var withClientKey = InternalClient(AcmeClientKey);
        var response = await withClientKey.PostAsync("/internal/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"order_placed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","user_id":"u-1"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Internal_token_is_rejected_on_the_public_listener()
    {
        // Symmetric guard: even though the internal token is a real secret, it
        // must not resolve on /v1/*. Keeps the resolvers cleanly separated so
        // no callsite accidentally accepts an internal-secret from a browser.
        using var withInternalToken = PublicClient(AcmeInternalKey);
        var response = await withInternalToken.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Emit_event_requires_a_valid_tenant_id()
    {
        // Unknown app_id passed to emit_event raises "unknown server event_name"
        // (that tenant has zero rows in event_registry).
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Db.EmitForApp(_ds, "no-such-tenant", "order_placed"));
        Assert.Contains("unknown server event_name", ex.MessageText);
    }

    [Fact]
    public async Task Query_endpoints_require_an_internal_token()
    {
        // Anonymous + client-key requests must be rejected before touching the
        // query. Otherwise the internal listener leaks every tenant's rows.
        using var anon = Client(_api.InternalBaseUri, "");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/internal/v1/query/events?from=2020-01-01T00:00:00Z")).StatusCode);

        using var withClientKey = InternalClient(AcmeClientKey);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await withClientKey.GetAsync("/internal/v1/query/events?from=2020-01-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await withClientKey.GetAsync($"/internal/v1/query/identity/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Query_events_returns_only_the_callers_tenant_rows()
    {
        await Db.EmitForApp(_ds, "acme", "order_placed");
        await Db.EmitForApp(_ds, "widgets", "order_placed");

        using var acme = InternalClient(AcmeInternalKey);
        var response = await acme.GetAsync("/internal/v1/query/events?from=2020-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Acme's caller sees acme's row(s), never widgets'. We assert the
        // negative case too — the leak that was in the original code would
        // return both, so an assertion on `acme` alone is not enough.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("events")
            .EnumerateArray()
            .Select(e => e.GetProperty("event_name").GetString())
            .ToArray();
        Assert.Contains("order_placed", names);
        // Two tenants both emitted `order_placed`; the outbox has two rows.
        // Without app_id scoping the caller would see 2; with scoping, exactly 1.
        Assert.Single(names);
    }

    [Fact]
    public async Task Query_events_never_leaks_another_tenants_user_attributes()
    {
        // Same user_id in both tenants with different emails. Before the fix,
        // the user_attributes join fanned out (missing AND ua.app_id = o.app_id)
        // and each acme event returned an extra row carrying widgets' email.
        await EventStore.UpsertUserAttributesAsync(_ds, "acme", "shared-42",
            """{"email":"ali@acme.example"}""", default);
        await EventStore.UpsertUserAttributesAsync(_ds, "widgets", "shared-42",
            """{"email":"ali@widgets.example"}""", default);
        await Db.EmitForApp(_ds, "acme", "order_placed", anonymousId: Guid.NewGuid());
        // Attach the user_id to acme's event row.
        await using (var upd = _ds.CreateCommand(
            "UPDATE events_outbox SET user_id = 'shared-42' WHERE app_id = 'acme'"))
            await upd.ExecuteNonQueryAsync();

        using var acme = InternalClient(AcmeInternalKey);
        var body = await (await acme.GetAsync("/internal/v1/query/events?from=2020-01-01T00:00:00Z"))
            .Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var events = doc.RootElement.GetProperty("events").EnumerateArray().ToArray();

        // Every row is acme's own email; widgets' value never appears. Before
        // the fix, each acme event fanned out to (email=acme, email=widgets)
        // pairs because the ua join lacked `AND ua.app_id = o.app_id`.
        Assert.NotEmpty(events);
        Assert.All(events, e =>
            Assert.Equal("ali@acme.example", e.GetProperty("email").GetString()));
        // And no distinct event_id appears more than once (fan-out symptom).
        var eventIds = events.Select(e => e.GetProperty("event_id").GetString()).ToArray();
        Assert.Equal(eventIds.Length, eventIds.Distinct().Count());
    }

    [Fact]
    public async Task Identity_upsert_from_one_tenant_cannot_overwrite_another_tenants_row()
    {
        // Reviewer's blocker #2: without the composite PK, tenant B posting an
        // identity with tenant A's session_key would overwrite A's row (app_id
        // stays 'acme' but every handle becomes B's). Worker's session_key join
        // then ships A's identifiers to B's destination accounts.
        var sharedSession = Guid.NewGuid();
        var acmeAnon = Guid.NewGuid();
        var widgetsAnon = Guid.NewGuid();

        string BodyFor(Guid anon, string cid) => $"{{\"session_key\":\"{sharedSession}\",\"anonymous_id\":\"{anon}\",\"session_number\":1,\"handles\":{{\"ga4_client_id\":\"{cid}\"}}}}";

        using var acme = PublicClient(AcmeClientKey);
        Assert.Equal(HttpStatusCode.NoContent,
            (await acme.PostAsync("/v1/identity", Body(BodyFor(acmeAnon, "acme-ga4-cid")))).StatusCode);

        using var widgets = PublicClient(WidgetsClientKey);
        Assert.Equal(HttpStatusCode.NoContent,
            (await widgets.PostAsync("/v1/identity", Body(BodyFor(widgetsAnon, "widgets-ga4-cid")))).StatusCode);

        // Both rows exist under their own tenants — no overwrite.
        Assert.Equal(2L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM identity_registry WHERE session_key = '{sharedSession}'"));
        Assert.Equal("acme-ga4-cid", await Db.Scalar<string>(_ds,
            $"SELECT ga4_client_id FROM identity_registry WHERE app_id = 'acme' AND session_key = '{sharedSession}'"));
        Assert.Equal("widgets-ga4-cid", await Db.Scalar<string>(_ds,
            $"SELECT ga4_client_id FROM identity_registry WHERE app_id = 'widgets' AND session_key = '{sharedSession}'"));
    }

    [Fact]
    public async Task Query_identity_scopes_to_the_callers_tenant()
    {
        // Widgets' internal token must not resolve an acme session key even
        // though session_key is a UUID and technically globally unique.
        var acmeSession = Guid.NewGuid();
        await using (var insert = _ds.CreateCommand(
            """
            INSERT INTO identity_registry (session_key, anonymous_id, app_id, session_number)
            VALUES ($1, $2, 'acme', 1)
            """))
        {
            insert.Parameters.Add(new() { Value = acmeSession });
            insert.Parameters.Add(new() { Value = Guid.NewGuid() });
            await insert.ExecuteNonQueryAsync();
        }

        using var widgets = InternalClient(WidgetsInternalKey);
        Assert.Equal(HttpStatusCode.NotFound,
            (await widgets.GetAsync($"/internal/v1/query/identity/{acmeSession}")).StatusCode);

        using var acme = InternalClient(AcmeInternalKey);
        Assert.Equal(HttpStatusCode.OK,
            (await acme.GetAsync($"/internal/v1/query/identity/{acmeSession}")).StatusCode);
    }

    [Fact]
    public async Task Internal_token_is_rejected_in_the_query_string()
    {
        // sendBeacon cannot set headers, so /v1/* accepts ?tenant_api_key=.
        // That concession must not extend to the server-side secret: URLs land
        // in access logs, Referer headers and proxy caches. Anything that reads
        // an internal_token must demand the Authorization header.
        using var anon = Client(_api.InternalBaseUri, "");

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(
            $"/internal/v1/query/events?from=2020-01-01T00:00:00Z&tenant_api_key={AcmeInternalKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(
            $"/internal/v1/query/identity/{Guid.NewGuid()}?tenant_api_key={AcmeInternalKey}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync(
            $"/internal/v1/events?tenant_api_key={AcmeInternalKey}", Body(
                $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"order_placed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","user_id":"u-1"}]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync(
            $"/internal/v1/user_attributes/acme/u-1?tenant_api_key={AcmeInternalKey}")).StatusCode);

        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE event_name = 'order_placed'"));
    }

    [Fact]
    public async Task Client_key_still_authenticates_from_the_query_string()
    {
        // The other half of the same rule: the sendBeacon path must keep
        // working, or every page-unload flush is silently dropped.
        using var anon = Client(_api.PublicBaseUri, "");
        var response = await anon.PostAsync($"/v1/events?tenant_api_key={AcmeClientKey}", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'acme'"));
    }

    [Fact]
    public async Task Another_tenants_origin_cannot_use_this_tenants_key()
    {
        // CORS on a shared listener can only allow the union of every tenant's
        // origins, so widgets' site passes the browser's check against acme's
        // endpoint. The app-layer check is what actually separates them.
        using var acme = PublicClient(AcmeClientKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = Body(
                $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""),
        };
        request.Headers.Add("Origin", "https://www.widgets.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await acme.SendAsync(request)).StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds, "SELECT count(*) FROM events_outbox"));
    }

    [Fact]
    public async Task Own_origin_is_accepted()
    {
        using var acme = PublicClient(AcmeClientKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = Body(
                $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""),
        };
        request.Headers.Add("Origin", "https://www.acme.example");

        Assert.Equal(HttpStatusCode.OK, (await acme.SendAsync(request)).StatusCode);
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'acme'"));
    }
}
