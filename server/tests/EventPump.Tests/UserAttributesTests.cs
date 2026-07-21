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
public class UserAttributesTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string PlanJson =
        """
        {
          "attributes": {
            "first_name": { "type": "string", "max_length": 128 },
            "last_name":  { "type": "string", "max_length": 128 },
            "email":      { "type": "email",  "max_length": 254 },
            "phone":      { "type": "e164",   "max_length": 16 },
            "gender":     { "type": "enum",   "values": ["male", "female", "other", "unknown"] },
            "city":       { "type": "string", "max_length": 128 }
          },
          "events": {
            "product_viewed": { "origin": "client", "destinations": ["ga4"] }
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
        _pub = Client(_api.PublicBaseUri, "tok-web");
        _int = Client(_api.InternalBaseUri, "internal-secret");
    }

    public async Task DisposeAsync()
    {
        _pub.Dispose();
        _int.Dispose();
        await _api.DisposeAsync();
    }

    private static HttpClient Client(Uri baseUri, string bearer)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = baseUri };
        client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return client;
    }

    private static EpConfig Config(bool moengageEnabled = true, bool moengageAttrs = true) => new()
    {
        DbConnString = "unused-in-tests",
        Listen = "http://127.0.0.1:0",
        InternalListen = "http://127.0.0.1:0",
        ClientTokens = new() { ["tok-web"] = "webapp" },
        InternalToken = "internal-secret",
        RateLimitPermits = 1000,
        RateLimitWindowSeconds = 60,
        MoEngageEnabled = moengageEnabled,
        MoEngageAttributesEnabled = moengageAttrs,
    };

    private Task<HttpResponseMessage> PostIdentity(string bodyJson)
        => _pub.PostAsync("/v1/identity", new StringContent(bodyJson, Encoding.UTF8, "application/json"));

    // ------------------------------------------------------ happy path

    [Fact]
    public async Task Attributes_land_normalized_and_stored_by_user_id()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();

        var response = await PostIdentity(
            $$"""
            {
              "session_key": "{{session}}",
              "anonymous_id": "{{anon}}",
              "user_id": "u-42",
              "attributes": {
                "first_name": "  Ali  ",
                "email": "ALI@Example.COM",
                "phone": "+9647701234567",
                "gender": "male",
                "city": "Baghdad"
              }
            }
            """);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Ali", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'first_name' FROM user_attributes WHERE user_id = 'u-42'"));
        Assert.Equal("ali@example.com", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'email' FROM user_attributes WHERE user_id = 'u-42'"));
        Assert.Equal("+9647701234567", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'phone' FROM user_attributes WHERE user_id = 'u-42'"));
        Assert.False(string.IsNullOrEmpty(await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-42'")));
    }

    [Fact]
    public async Task Partial_upserts_merge_and_null_clears_a_key()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        string Body(string attributesJson) => $$"""
            {
              "session_key": "{{session}}",
              "anonymous_id": "{{anon}}",
              "user_id": "u-merge",
              "attributes": {{attributesJson}}
            }
            """;

        Assert.Equal(HttpStatusCode.NoContent,
            (await PostIdentity(Body("""{ "first_name": "Ali", "email": "ali@example.com" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostIdentity(Body("""{ "phone": "+9647701234567" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostIdentity(Body("""{ "email": null }"""))).StatusCode);

        Assert.Equal("Ali", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'first_name' FROM user_attributes WHERE user_id = 'u-merge'"));
        Assert.Equal("+9647701234567", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'phone' FROM user_attributes WHERE user_id = 'u-merge'"));
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id = 'u-merge' AND attributes ? 'email'"));
    }

    [Fact]
    public async Task Hash_stays_stable_when_the_merged_state_does_not_change()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        var body = $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-hash",
              "attributes": { "email": "a@b.co" } }
            """;

        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(body)).StatusCode);
        var first = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-hash'");
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(body)).StatusCode);
        var second = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-hash'");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task User_id_resolves_from_registry_when_absent_from_body()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""{ "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-later" }""")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}",
              "attributes": { "first_name": "Ali" } }
            """)).StatusCode);
        Assert.Equal("Ali", await Db.Scalar<string>(_ds,
            "SELECT attributes->>'first_name' FROM user_attributes WHERE user_id = 'u-later'"));
    }

    // ------------------------------------------------------- rejection

    [Fact]
    public async Task Attributes_without_any_resolvable_user_id_reject_400()
    {
        var response = await PostIdentity(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "attributes": { "email": "a@b.co" } }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("attributes_require_user_id", body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{ "ssn": "123" }""", "unknown_attribute:ssn")]
    [InlineData("""{ "phone": "9647701234567" }""", "invalid_attribute:phone")]
    [InlineData("""{ "email": "not-an-email" }""", "invalid_attribute:email")]
    [InlineData("""{ "gender": "unspecified" }""", "invalid_attribute:gender")]
    public async Task Each_normalization_failure_maps_to_its_own_rejection_code(string attributesJson, string expected)
    {
        var response = await PostIdentity(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "user_id": "u-bad", "attributes": {{attributesJson}} }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(expected, body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Attributes_block_over_4kb_is_rejected()
    {
        var huge = new string('x', 5000);
        var response = await PostIdentity(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "user_id": "u-huge", "attributes": { "first_name": "{{huge}}" } }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // The oversized value fails the length cap first — either way it's a 400.
        var detail = body.RootElement.GetProperty("detail").GetString()!;
        Assert.True(detail.StartsWith("invalid_attribute:") || detail == "attributes_too_large");
    }

    // ----------------------------------------------- storage-level direct

    [Fact]
    public async Task UpsertUserAttributesAsync_returns_previous_synced_hash()
    {
        const string userId = "u-direct";
        var first = await EventStore.UpsertUserAttributesAsync(
            _ds, userId, """{"email":"a@b.co"}""", CancellationToken.None);
        Assert.Null(first.PreviousSyncedHash);

        // Simulate the MoEngage sender's write-back of the hash it actually delivered.
        await Db.Exec(_ds,
            $"UPDATE user_attributes SET moengage_synced_hash = hash, moengage_synced_at = now() WHERE user_id = '{userId}'");

        var second = await EventStore.UpsertUserAttributesAsync(
            _ds, userId, """{"phone":"+9647701234567"}""", CancellationToken.None);
        Assert.NotNull(second.PreviousSyncedHash);
        Assert.NotEqual(second.NewHash, second.PreviousSyncedHash);
    }

    [Fact]
    public async Task DeleteUserAttributesAsync_is_idempotent()
    {
        await EventStore.UpsertUserAttributesAsync(
            _ds, "u-dsr", """{"email":"a@b.co"}""", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "u-dsr", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "u-dsr", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "never-existed", CancellationToken.None);

        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id IN ('u-dsr', 'never-existed')"));
    }

    // ------------------------------------- moengage_customer sync enqueue

    private static string SyncOutboxCount(string userId)
        => $"SELECT count(*) FROM events_outbox WHERE event_name = 'ep_attributes_synced' AND user_id = '{userId}'";

    [Fact]
    public async Task Hash_change_enqueues_a_moengage_customer_delivery()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-sync",
              "attributes": { "email": "a@b.co" } }
            """)).StatusCode);

        Assert.Equal(1L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-sync")));
        var enqueuedId = await Db.Scalar<Guid>(_ds,
            "SELECT event_id FROM events_outbox WHERE event_name = 'ep_attributes_synced' AND user_id = 'u-sync'");
        Assert.Equal(["moengage_customer"], await Db.DeliveryDestinations(_ds, enqueuedId));
    }

    [Fact]
    public async Task Same_hash_does_not_re_enqueue_after_sender_write_back()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        var body = $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-once",
              "attributes": { "email": "a@b.co" } }
            """;

        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(body)).StatusCode);
        // Simulate the MoEngage sender's post-delivered hash write-back.
        await Db.Exec(_ds,
            "UPDATE user_attributes SET moengage_synced_hash = hash, moengage_synced_at = now() WHERE user_id = 'u-once'");
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(body)).StatusCode);

        Assert.Equal(1L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-once")));
    }

    [Fact]
    public async Task Sync_does_not_enqueue_when_moengage_attributes_flag_is_off()
    {
        await using var offApi = await ApiApp.StartAsync(
            Config(moengageAttrs: false), _ds, _plan, new MetricsRegistry());
        using var pub = Client(offApi.PublicBaseUri, "tok-web");

        var response = await pub.PostAsync("/v1/identity", new StringContent(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "user_id": "u-off", "attributes": { "email": "a@b.co" } }
            """, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id = 'u-off'"));
        Assert.Equal(0L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-off")));
    }

    [Fact]
    public async Task Sync_does_not_enqueue_when_moengage_destination_disabled_globally()
    {
        await using var offApi = await ApiApp.StartAsync(
            Config(moengageEnabled: false), _ds, _plan, new MetricsRegistry());
        using var pub = Client(offApi.PublicBaseUri, "tok-web");

        Assert.Equal(HttpStatusCode.NoContent, (await pub.PostAsync("/v1/identity", new StringContent(
            $$"""
            { "session_key": "{{Guid.NewGuid()}}", "anonymous_id": "{{Guid.NewGuid()}}",
              "user_id": "u-nome", "attributes": { "email": "a@b.co" } }
            """, Encoding.UTF8, "application/json"))).StatusCode);

        Assert.Equal(0L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-nome")));
    }

    // ---------------------------------------------------- DSR endpoint

    [Fact]
    public async Task Dsr_delete_removes_row_and_is_idempotent()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "u-dsr-http", """{"email":"a@b.co"}""", default);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/u-dsr-http")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/u-dsr-http")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/never-existed")).StatusCode);

        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id = 'u-dsr-http'"));
    }

    [Fact]
    public async Task Dsr_delete_requires_internal_token_and_internal_listener()
    {
        // wrong port
        Assert.Equal(HttpStatusCode.NotFound,
            (await _pub.DeleteAsync("/internal/v1/user_attributes/u-dsr")).StatusCode);
        // client bearer on internal listener is not accepted
        using var wrongAuth = Client(_api.InternalBaseUri, "tok-web");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrongAuth.DeleteAsync("/internal/v1/user_attributes/u-dsr")).StatusCode);
    }
}
