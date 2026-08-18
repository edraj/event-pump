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
        await RegistrySync.SyncTenantAsync(_ds, "zainmart", _plan);
        _api = await ApiApp.StartAsync(Config(), _ds, Tenants(_plan), new MetricsRegistry());
        _pub = Client(_api.PublicBaseUri, "client-key");
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

    private static EpConfig Config() => new()
    {
        DbConnString = "unused-in-tests",
        Listen = "http://127.0.0.1:0",
        InternalListen = "http://127.0.0.1:0",
    };

    private static TenantRegistry Tenants(TrackingPlan plan, bool moengageEnabled = true, bool moengageAttrs = true)
        => TenantRegistry.ForTesting(new TenantConfig
        {
            AppId = "zainmart",
            TenantApiKey = "client-key",
            InternalToken = "internal-secret",
            RateLimitPermits = 1000,
            RateLimitWindowSeconds = 60,
            MoEngageEnabled = moengageEnabled,
            MoEngageAttributesEnabled = moengageAttrs,
            Plan = plan,
        });

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
    public async Task Null_on_a_first_ever_upsert_stores_nothing_and_does_not_enqueue()
    {
        // Regression: the INSERT branch used to skip jsonb_strip_nulls, so a
        // null arriving before the row existed was stored as a real key. The
        // row then hashed non-empty, enqueued a MoEngage sync, and shipped
        // `attributes: {"email": null}` for a user who never set an attribute.
        // Reachable from both SDKs: setUserAttributes({email: null}) posts.
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-fresh-null",
              "attributes": { "email": null } }
            """)).StatusCode);

        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id = 'u-fresh-null' AND attributes ? 'email'"));
        Assert.Equal("{}", await Db.Scalar<string>(_ds,
            "SELECT attributes::text FROM user_attributes WHERE user_id = 'u-fresh-null'"));
        // An empty merged state is not worth a delivery row — the sender could
        // only ever resolve it to `skipped: no_attributes`.
        Assert.Equal(0L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-fresh-null")));
    }

    [Fact]
    public async Task Attributes_require_user_id_rejects_before_the_identity_write()
    {
        // Both attribute rejection classes must behave alike: 400, nothing
        // persisted. This one used to land after UpsertIdentityAsync had
        // already committed the registry row.
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        var response = await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}",
              "attributes": { "email": "a@b.co" } }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM identity_registry WHERE session_key = '{session}'"));
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
            _ds, "zainmart", userId, """{"email":"a@b.co"}""", CancellationToken.None);
        Assert.Null(first.PreviousSyncedHash);

        // Simulate the MoEngage sender's write-back of the hash it actually delivered.
        await Db.Exec(_ds,
            $"UPDATE user_attributes SET moengage_synced_hash = hash, moengage_synced_at = now() WHERE user_id = '{userId}'");

        var second = await EventStore.UpsertUserAttributesAsync(
            _ds, "zainmart", userId, """{"phone":"+9647701234567"}""", CancellationToken.None);
        Assert.NotNull(second.PreviousSyncedHash);
        Assert.NotEqual(second.NewHash, second.PreviousSyncedHash);
    }

    [Fact]
    public async Task DeleteUserAttributesAsync_is_idempotent()
    {
        await EventStore.UpsertUserAttributesAsync(
            _ds, "zainmart", "u-dsr", """{"email":"a@b.co"}""", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "zainmart", "u-dsr", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "zainmart", "u-dsr", CancellationToken.None);
        await EventStore.DeleteUserAttributesAsync(_ds, "zainmart", "never-existed", CancellationToken.None);

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
    public async Task Sync_falls_back_to_stored_moengage_customer_id_when_handles_not_re_sent()
    {
        // PR #8 review open-question #6. Real-world shape: the client calls
        // identify({handles: {moengage_customer_id: 'MOE-42'}}) at login,
        // then later calls setUserAttributes({...}) without re-sending
        // handles. Before the fix, the enqueued sync row carried NULL and
        // the MoEngage customer sender fell back to user_id, creating the
        // second profile the handle was supposed to prevent.
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();

        // Step 1: register the session with the moengage_customer_id handle.
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-moe",
              "handles": { "moengage_customer_id": "MOE-42" } }
            """)).StatusCode);

        // Step 2: set attributes WITHOUT re-sending handles. Server must
        // still stash MOE-42 on the outbox row, not NULL.
        Assert.Equal(HttpStatusCode.NoContent, (await PostIdentity(
            $$"""
            { "session_key": "{{session}}", "anonymous_id": "{{anon}}", "user_id": "u-moe",
              "attributes": { "email": "a@b.co" } }
            """)).StatusCode);

        Assert.Equal(1L, await Db.Scalar<long>(_ds, SyncOutboxCount("u-moe")));
        Assert.Equal("MOE-42", await Db.Scalar<string>(_ds,
            "SELECT context->>'moengage_customer_id' FROM events_outbox " +
            "WHERE event_name = 'ep_attributes_synced' AND user_id = 'u-moe'"));
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
            Config(), _ds, Tenants(_plan, moengageAttrs: false), new MetricsRegistry());
        using var pub = Client(offApi.PublicBaseUri, "client-key");

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
            Config(), _ds, Tenants(_plan, moengageEnabled: false), new MetricsRegistry());
        using var pub = Client(offApi.PublicBaseUri, "client-key");

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
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-dsr-http", """{"email":"a@b.co"}""", default);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/zainmart/u-dsr-http")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/zainmart/u-dsr-http")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _int.DeleteAsync("/internal/v1/user_attributes/zainmart/never-existed")).StatusCode);

        Assert.Equal(0L, await Db.Scalar<long>(_ds,
            "SELECT count(*) FROM user_attributes WHERE user_id = 'u-dsr-http'"));
    }

    [Fact]
    public async Task Dsr_delete_lives_only_on_internal_listener()
    {
        // wrong port: DSR endpoint is 404 on the public listener even with a
        // valid tenant_api_key.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _pub.DeleteAsync("/internal/v1/user_attributes/zainmart/u-dsr")).StatusCode);
        // Unknown bearer on the internal listener is 401.
        using var stranger = Client(_api.InternalBaseUri, "unknown-key");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await stranger.DeleteAsync("/internal/v1/user_attributes/zainmart/u-dsr")).StatusCode);
    }
}
