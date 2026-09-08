using System.Net;
using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ErasureApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string PlanJson =
        """
        {
          "attributes": { "city": { "type": "string", "max_length": 128 } },
          "events": { "product_viewed": { "origin": "client", "destinations": ["moengage"] } }
        }
        """;

    private NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private HttpClient _pub = null!;
    private HttpClient _int = null!;

    public async Task InitializeAsync()
    {
        _ds = await pg.CreateMigratedDatabaseAsync();
        var plan = TrackingPlan.Parse(PlanJson);
        await RegistrySync.SyncTenantAsync(_ds, "zainmart", plan);
        _api = await ApiApp.StartAsync(Config(), _ds, Tenants(plan), new MetricsRegistry());
        _pub = Client(_api.PublicBaseUri, "client-key");
        _int = Client(_api.InternalBaseUri, "internal-secret");
    }

    public async Task DisposeAsync()
    {
        _pub.Dispose();
        _int.Dispose();
        await _api.DisposeAsync();
        await _ds.DisposeAsync();
    }

    private static HttpClient Client(Uri baseUri, string bearer)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = baseUri };
        client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return client;
    }

    private static EpConfig Config() => new()
    {
        DbConnString = "unused-in-tests",
        Listen = "http://127.0.0.1:0",
        InternalListen = "http://127.0.0.1:0",
    };

    private static TenantRegistry Tenants(
        TrackingPlan plan, bool moengage = true, bool adjust = true, bool erasure = true)
        => TenantRegistry.ForTesting(new TenantConfig
        {
            AppId = "zainmart",
            TenantApiKey = "client-key",
            InternalToken = "internal-secret",
            RateLimitPermits = 1000,
            RateLimitWindowSeconds = 60,
            MoEngageEnabled = moengage,
            // Credentials the senders never reach in these tests, but a
            // destination enabled without them does not boot (TenantRegistry).
            MoEngageAppId = "MOE-APP",
            MoEngageApiKey = "moe-key",
            MoEngageErasureEnabled = erasure,
            AdjustEnabled = adjust,
            AdjustAppToken = "adj-token",
            AdjustErasureEnabled = erasure,
            Plan = plan,
        });

    private Task<HttpResponseMessage> Erase(string path) =>
        _int.PostAsync(path, content: null);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private Task SeedAttributes(string userId) => Db.Exec(_ds,
        $"INSERT INTO user_attributes (app_id, user_id, attributes) " +
        $"VALUES ('zainmart', '{userId}', '{{\"city\":\"baghdad\"}}')");

    private Task<long> AttributeRows(string userId) => Db.Scalar<long>(_ds,
        $"SELECT count(*) FROM user_attributes WHERE app_id='zainmart' AND user_id='{userId}'");

    private Task<long> AuditRows() => Db.Scalar<long>(_ds, "SELECT count(*) FROM erasure_audit");

    [Fact]
    public async Task Rejects_a_request_with_no_authorization_header()
    {
        using var anonymous = new HttpClient { BaseAddress = _api.InternalBaseUri };

        var response = await anonymous.PostAsync("/internal/v1/erasure/zainmart/u-1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_the_client_key_on_the_internal_listener()
    {
        using var wrong = Client(_api.InternalBaseUri, "client-key");

        var response = await wrong.PostAsync("/internal/v1/erasure/zainmart/u-1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_token_that_does_not_own_the_app_id_in_the_url()
    {
        var response = await Erase("/internal/v1/erasure/someone-else/u-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Is_not_reachable_on_the_public_listener()
    {
        var response = await _pub.PostAsync("/internal/v1/erasure/zainmart/u-1", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Queues_every_enabled_destination_and_deletes_the_local_row()
    {
        await SeedAttributes("u-1");

        var response = await Erase("/internal/v1/erasure/zainmart/u-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.Equal(
            ["adjust_erasure", "moengage_erasure"],
            body.GetProperty("destinations").EnumerateArray()
                .Select(d => d.GetString()!).Order());
        Assert.Equal(0, await AttributeRows("u-1"));
    }

    [Fact]
    public async Task Records_an_audit_row_for_every_request()
    {
        await Erase("/internal/v1/erasure/zainmart/u-1");

        Assert.Equal(1, await AuditRows());
    }

    [Fact]
    public async Task The_attributes_variant_queues_under_its_own_event()
    {
        var response = await Erase("/internal/v1/erasure/zainmart/u-1/attributes");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE event_name = 'ep_attributes_erasure_requested'");
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task Sends_the_handle_the_destination_knows_the_person_by()
    {
        await Db.Exec(_ds,
            """
            INSERT INTO identity_registry
                (session_key, anonymous_id, app_id, user_id, moengage_customer_id)
            VALUES (gen_random_uuid(), gen_random_uuid(), 'zainmart', 'u-1', 'M-42')
            """);

        await Erase("/internal/v1/erasure/zainmart/u-1");

        var context = await Db.Scalar<string>(_ds,
            "SELECT context::text FROM events_outbox WHERE event_name = 'ep_erasure_requested'");
        Assert.Contains("M-42", context);
    }

    [Fact]
    public async Task Narrows_to_the_destinations_the_caller_named()
    {
        var response = await Erase("/internal/v1/erasure/zainmart/u-1?destinations=adjust_erasure");

        var body = await Json(response);
        Assert.Equal(
            ["adjust_erasure"],
            body.GetProperty("destinations").EnumerateArray().Select(d => d.GetString()!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("  ,  ")]
    public async Task Rejects_a_destinations_parameter_that_names_nothing(string value)
    {
        // Without this the empty list slips past the unknown-destination check
        // and answers 202 "accepted" having queued nowhere. Asking for every
        // destination is spelled by leaving the parameter off.
        var response = await Erase($"/internal/v1/erasure/zainmart/u-1?destinations={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await AuditRows());
        Assert.Equal(0, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE event_name = 'ep_erasure_requested'"));
    }

    [Fact]
    public async Task Rejects_a_destination_this_tenant_does_not_erase_to()
    {
        var response = await Erase("/internal/v1/erasure/zainmart/u-1?destinations=ga4_erasure");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.Scalar<long>(_ds, "SELECT count(*) FROM erasure_audit"));
    }

    [Fact]
    public async Task Repeating_the_call_does_not_queue_a_second_erasure()
    {
        await Erase("/internal/v1/erasure/zainmart/u-1");

        var again = await Erase("/internal/v1/erasure/zainmart/u-1");

        var body = await Json(again);
        Assert.Empty(body.GetProperty("destinations").EnumerateArray());
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    [Fact]
    public async Task Cancels_a_delivery_still_queued_for_that_person()
    {
        await Db.RegisterEvent(_ds, "product_viewed", "server", "moengage");
        var eventId = Guid.NewGuid();
        await Db.Emit(_ds, "product_viewed", eventId);
        await Db.Exec(_ds, $"UPDATE events_outbox SET user_id = 'u-1' WHERE event_id = '{eventId}'");

        var response = await Erase("/internal/v1/erasure/zainmart/u-1");

        var body = await Json(response);
        Assert.Equal(1, body.GetProperty("cancelled_deliveries").GetInt32());
    }

    private Task<long> IdentityRows(string userId) => Db.Scalar<long>(_ds,
        $"SELECT count(*) FROM identity_registry WHERE app_id='zainmart' AND user_id='{userId}'");

    private Task SeedIdentity(string userId, string moengage) => Db.Exec(_ds,
        "INSERT INTO identity_registry (session_key, anonymous_id, app_id, user_id, " +
        $"moengage_customer_id) VALUES (gen_random_uuid(), gen_random_uuid(), 'zainmart', " +
        $"'{userId}', '{moengage}')");

    [Fact]
    public async Task Person_erasure_also_removes_the_identity_registry_row()
    {
        await SeedIdentity("u-1", "M-1");
        Assert.Equal(1, await IdentityRows("u-1"));

        await Erase("/internal/v1/erasure/zainmart/u-1");

        Assert.Equal(0, await IdentityRows("u-1"));
    }

    [Fact]
    public async Task Attributes_erasure_keeps_the_identity_registry_row()
    {
        await SeedIdentity("u-1", "M-1");

        await Erase("/internal/v1/erasure/zainmart/u-1/attributes");

        Assert.Equal(1, await IdentityRows("u-1"));
    }

    [Fact]
    public async Task A_second_erasure_still_names_the_person_downstream()
    {
        await SeedIdentity("u-1", "M-1");
        await Erase("/internal/v1/erasure/zainmart/u-1");
        await Db.Exec(_ds, "UPDATE events_delivery SET status='delivered' " +
                           "WHERE destination LIKE '%erasure%'");

        await Erase("/internal/v1/erasure/zainmart/u-1");

        var contexts = await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM events_outbox WHERE event_name='ep_erasure_requested' " +
            "AND context->>'moengage_customer_id' = 'M-1'");
        Assert.Equal(2, contexts);
    }

    [Fact]
    public async Task Audit_history_is_readable_over_http()
    {
        await SeedIdentity("u-1", "M-1");
        await Erase("/internal/v1/erasure/zainmart/u-1");

        var response = await _int.GetAsync("/internal/v1/erasure/zainmart/u-1/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var row = body.EnumerateArray().Single();
        Assert.Equal("person", row.GetProperty("variant").GetString());
        Assert.Contains("M-1", row.GetProperty("handles").ToString());
    }

    [Fact]
    public async Task Audit_history_is_scoped_to_the_tokens_tenant()
    {
        var response = await _int.GetAsync("/internal/v1/erasure/other/u-1/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Still_deletes_locally_when_the_tenant_erases_nowhere()
    {
        await _api.DisposeAsync();
        _int.Dispose();
        var plan = TrackingPlan.Parse(PlanJson);
        _api = await ApiApp.StartAsync(
            Config(), _ds, Tenants(plan, moengage: false, adjust: false), new MetricsRegistry());
        _int = Client(_api.InternalBaseUri, "internal-secret");
        await SeedAttributes("u-1");

        var response = await Erase("/internal/v1/erasure/zainmart/u-1");

        var body = await Json(response);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(body.GetProperty("destinations").EnumerateArray());
        Assert.Equal(0, await AttributeRows("u-1"));
        Assert.Equal(1, await AuditRows());
    }
}
