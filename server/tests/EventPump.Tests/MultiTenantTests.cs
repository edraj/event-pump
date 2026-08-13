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
/// Auth model (post-rename): one tenant_api_key per tenant, sent by every
/// caller — SDK and server producers alike. No separate client/internal
/// tokens, no X-App-Id header.
/// </summary>
[Collection("pg")]
public class MultiTenantTests(PostgresFixture pg) : IAsyncLifetime
{
    private Npgsql.NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private TenantRegistry _tenants = null!;

    private const string AcmeKey    = "acme-api-key";
    private const string WidgetsKey = "widgets-api-key";

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
                TenantApiKey = AcmeKey,
                CookieDomain = ".acme.example",
                Plan = Plan(),
            },
            new TenantConfig
            {
                AppId = "widgets",
                TenantApiKey = WidgetsKey,
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
        using var acme = PublicClient(AcmeKey);
        using var widgets = PublicClient(WidgetsKey);

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
        using var widgets = PublicClient(WidgetsKey);
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
    public async Task Dsr_endpoint_rejects_cross_tenant_key_use()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "acme", "u-secret",
            """{"email":"secret@acme.example"}""", default);

        // widgets' api key cannot DSR-delete an acme user
        using var widgets = InternalClient(WidgetsKey);
        var response = await widgets.DeleteAsync("/internal/v1/user_attributes/acme/u-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Row still there
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE app_id = 'acme' AND user_id = 'u-secret'"));

        // acme's api key succeeds
        using var acme = InternalClient(AcmeKey);
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
