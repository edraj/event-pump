using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using EventPump.Observability;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// GET /internal/v1/query/tenant — the events UI's "who am I?" call. The bearer
/// is the only thing that selects a tenant (there is no ?app_id=), so this
/// endpoint is what lets the UI label its rows and build filters out of values
/// that can actually match. No database is involved: everything it answers with
/// comes from the caller's own TenantConfig.
/// </summary>
public class TenantInfoTests : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;

    private const string AcmeInternal = "acme-internal-secret";
    private const string AcmeClient = "acme-client-key";
    private const string WidgetsInternal = "widgets-internal-secret";

    public async Task InitializeAsync()
    {
        _ds = DocsHost.DataSource();
        var plan = TrackingPlan.Parse(
            """
            {
              "attributes": { "email": { "type": "email" }, "city": { "type": "string" } },
              "events": {
                "product_viewed": { "origin": "client", "destinations": ["ga4"] },
                "order_placed":   { "origin": "server", "destinations": ["ga4"] }
              }
            }
            """);
        _api = await ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
            QueryMaxDays = 3,
        }, _ds, TenantRegistry.ForTesting(
            new TenantConfig
            {
                AppId = "acme",
                TenantApiKey = AcmeClient,
                InternalToken = AcmeInternal,
                Plan = plan,
                Ga4Enabled = true,
                Ga4AttributesEnabled = true,
                MoEngageEnabled = true,
                MoEngageAttributesEnabled = false,
                // left off on purpose — an absent destination must not be offered
                AdjustEnabled = false,
            },
            new TenantConfig
            {
                AppId = "widgets",
                TenantApiKey = "widgets-client-key",
                InternalToken = WidgetsInternal,
                Plan = TrackingPlan.Parse("""{"events":{"signup":{"origin":"client","destinations":[]}}}"""),
                AmplitudeEnabled = true,
            }), new MetricsRegistry());
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _ds.DisposeAsync();
    }

    private HttpClient Internal(string token)
    {
        var client = new HttpClient { BaseAddress = _api.InternalBaseUri };
        if (token.Length > 0)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<JsonElement> GetAsync(string token)
    {
        using var client = Internal(token);
        var response = await client.GetAsync("/internal/v1/query/tenant");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>
    /// The whole point: the token, and only the token, decides which tenant is
    /// described. Two callers on the same listener get two different answers.
    /// </summary>
    [Fact]
    public async Task The_token_selects_which_tenant_is_described()
    {
        Assert.Equal("acme", (await GetAsync(AcmeInternal)).GetProperty("app_id").GetString());
        Assert.Equal("widgets", (await GetAsync(WidgetsInternal)).GetProperty("app_id").GetString());
    }

    /// <summary>
    /// Only the destinations the tenant actually runs, mirroring the pipelines
    /// SenderFactory materialises — moengage_customer rides along with moengage,
    /// so delivery rows carry it and the UI's filter has to offer it. A
    /// destination that is off must not appear, or the filter offers a value
    /// that can only ever return an empty page.
    /// </summary>
    [Fact]
    public async Task Only_enabled_destinations_are_listed_and_moengage_brings_its_customer_pipeline()
    {
        var destinations = (await GetAsync(AcmeInternal)).GetProperty("destinations")
            .EnumerateArray()
            .ToDictionary(d => d.GetProperty("code").GetString()!,
                          d => d.GetProperty("attributes_enabled").GetBoolean());

        Assert.Equal(
            new[] { "ga4", "moengage", "moengage_customer" }.Order(),
            destinations.Keys.Order());
        // SPEC §6.1: the gate is per destination, and the UI has to be able to
        // say why attribute-derived fields are missing on one but not another.
        Assert.True(destinations["ga4"]);
        Assert.False(destinations["moengage"]);

        var widgets = (await GetAsync(WidgetsInternal)).GetProperty("destinations")
            .EnumerateArray().Select(d => d.GetProperty("code").GetString()!).ToArray();
        Assert.Equal(["amplitude"], widgets);
    }

    /// <summary>
    /// The plan's event names and attribute allowlist, so the UI offers pickers
    /// rather than free-text boxes. `ep_attributes_synced` is injected by
    /// TrackingPlan.Parse and is a real routable name, so it belongs in the list.
    /// </summary>
    [Fact]
    public async Task Plan_events_and_attributes_come_back_sorted()
    {
        var tenant = await GetAsync(AcmeInternal);

        var events = tenant.GetProperty("events").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(
            [
                "ep_attributes_erasure_requested", "ep_attributes_synced",
                "ep_erasure_requested", "order_placed", "product_viewed",
            ],
            events);

        var attributes = tenant.GetProperty("attributes").EnumerateArray()
            .Select(a => (a.GetProperty("name").GetString(), a.GetProperty("type").GetString()))
            .ToArray();
        Assert.Equal([("city", "string"), ("email", "email")], attributes);
    }

    /// <summary>
    /// The UI clamps its own date pickers to this. If it did not travel, a
    /// 30-day range would silently come back as EP_QUERY_MAX_DAYS of rows.
    /// </summary>
    [Fact]
    public async Task Query_window_ceiling_travels_to_the_client()
        => Assert.Equal(3, (await GetAsync(AcmeInternal)).GetProperty("query_max_days").GetInt32());

    /// <summary>
    /// It describes a tenant, so it must never echo one's credentials — not the
    /// client key that ships in SDK bundles, not the server secret, not the
    /// destination api secrets and app tokens sitting next to them in the file.
    /// </summary>
    [Fact]
    public async Task No_credential_is_echoed_back()
    {
        using var client = Internal(AcmeInternal);
        var body = await client.GetStringAsync("/internal/v1/query/tenant");

        Assert.DoesNotContain(AcmeInternal, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AcmeClient, body, StringComparison.Ordinal);
        foreach (var forbidden in (string[])["api_secret", "api_key", "app_token", "access_token", "token"])
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same gate as the other query routes: the client key ships inside app
    /// bundles and must not reach the internal listener, and an anonymous
    /// caller must not be able to enumerate a deployment's tenants.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(AcmeClient)]
    [InlineData("wrong")]
    public async Task Only_an_internal_token_is_accepted(string token)
    {
        using var client = Internal(token);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/internal/v1/query/tenant")).StatusCode);
    }

    /// <summary>The internal listener owns it; the public one must 404.</summary>
    [Fact]
    public async Task Not_answered_on_the_public_listener()
    {
        using var client = new HttpClient { BaseAddress = _api.PublicBaseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AcmeInternal);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/internal/v1/query/tenant")).StatusCode);
    }
}
