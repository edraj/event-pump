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
/// other's rows. This suite exercises the isolation seams: token → app_id
/// routing, event_registry composite key, user_attributes composite key,
/// DSR endpoint scoping, and worker claim narrowing.
/// </summary>
[Collection("pg")]
public class MultiTenantTests(PostgresFixture pg) : IAsyncLifetime
{
    private Npgsql.NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private TenantRegistry _tenants = null!;

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
                ClientTokens = ["acme-tok"],
                InternalToken = "acme-internal",
                CookieDomain = ".acme.example",
                Plan = Plan(),
            },
            new TenantConfig
            {
                AppId = "widgets",
                ClientTokens = ["widgets-tok"],
                InternalToken = "widgets-internal",
                CookieDomain = ".widgets.example",
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

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private HttpClient PublicClient(string bearer, string? appId = null)
    {
        var c = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = _api.PublicBaseUri };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        // Default the header to the token's own tenant; individual tests
        // that want to exercise a mismatch override.
        c.DefaultRequestHeaders.Add("X-App-Id", appId ?? bearer.Split('-')[0]);
        return c;
    }

    private HttpClient InternalClient(string bearer)
    {
        var c = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = _api.InternalBaseUri };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return c;
    }

    private static StringContent Body(string bodyJson) => new(bodyJson, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Two_tenants_ingest_into_separate_app_id_rows()
    {
        using var acme = PublicClient("acme-tok");
        using var widgets = PublicClient("widgets-tok");

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
    public async Task One_tenant_accepts_multiple_client_app_ids_mobile_and_web()
    {
        // Spin a separate registry + API where the "omni" tenant has three
        // faces: its mobile Flutter app (pubspec name), its web site
        // (document domain), and its mobile web variant.
        var plan = Plan();
        var omni = new TenantConfig
        {
            AppId = "omni",
            ClientTokens = ["omni-tok"],
            ClientAppIds = ["omni_mobile", "www.omni.example", "m.omni.example"],
            InternalToken = "omni-internal",
            Plan = plan,
        };
        var registry = TenantRegistry.ForTesting(omni);
        await RegistrySync.SyncAllAsync(_ds, registry);
        await using var api = await ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
        }, _ds, registry, new MetricsRegistry());

        HttpClient WithFace(string face)
        {
            var c = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = api.PublicBaseUri };
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "omni-tok");
            c.DefaultRequestHeaders.Add("X-App-Id", face);
            return c;
        }

        foreach (var face in new[] { "omni_mobile", "www.omni.example", "m.omni.example" })
        {
            using var client = WithFace(face);
            var response = await client.PostAsync("/v1/events", Body(
                $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // A face NOT in the tenant's client_app_ids is rejected 401
        using (var rogue = WithFace("beta.omni.example"))
        {
            var response = await rogue.PostAsync("/v1/events", Body(
                $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // All 3 valid faces posted one event each; the rogue posted zero
        Assert.Equal(3L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id = 'omni'"));
    }

    [Fact]
    public async Task Missing_x_app_id_header_is_rejected_with_401()
    {
        // Valid bearer but no X-App-Id header - defense in depth (SPEC §9.1).
        using var noHeader = new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            BaseAddress = _api.PublicBaseUri,
        };
        noHeader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "acme-tok");
        var response = await noHeader.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_x_app_id_header_for_valid_token_is_rejected_with_401()
    {
        // A leaked acme token sent by an app that claims to be widgets - 401.
        using var mismatched = PublicClient("acme-tok", appId: "widgets");
        var response = await mismatched.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"product_viewed","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE app_id IN ('acme','widgets')"));
    }

    [Fact]
    public async Task Unknown_token_is_rejected_with_401()
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
        using var widgets = PublicClient("widgets-tok");
        var response = await widgets.PostAsync("/v1/events", Body(
            $$"""{"events":[{"event_id":"{{Guid.NewGuid()}}","event_name":"widget_only","occurred_at":"{{DateTimeOffset.UtcNow:O}}","anonymous_id":"{{Guid.NewGuid()}}"}]}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // widgets' inserted-count is zero because the name was rejected at validation
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"accepted\":0", body);
        Assert.Contains("unknown_event_name", body);
        // No widgets row landed
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
    public async Task Dsr_endpoint_rejects_cross_tenant_token_use()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "acme", "u-secret",
            """{"email":"secret@acme.example"}""", default);

        // widgets' internal token cannot DSR-delete an acme user
        using var widgets = InternalClient("widgets-internal");
        var response = await widgets.DeleteAsync("/internal/v1/user_attributes/acme/u-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Row still there
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE app_id = 'acme' AND user_id = 'u-secret'"));

        // acme's internal token succeeds
        using var acme = InternalClient("acme-internal");
        Assert.Equal(HttpStatusCode.NoContent,
            (await acme.DeleteAsync("/internal/v1/user_attributes/acme/u-secret")).StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE app_id = 'acme' AND user_id = 'u-secret'"));
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
}
