using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Data;
using EventPump.Senders;
using EventPump.Worker;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class MoEngageCustomerSenderTests(PostgresFixture pg) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync() => _ds = await pg.CreateMigratedDatabaseAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    // ----------------------------------------------------- shared helpers

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Requests.Add((r, body));
            return responder(r);
        }
    }

    private static StubHandler Ok() =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

    // MoEngageCustomerSender does not consult the plan at delivery time
    // (attribute names are canonical), so an empty plan satisfies TenantConfig.
    private static readonly TrackingPlan EmptyPlan = TrackingPlan.Parse("{}");

    private static EpConfig Config(bool attributesEnabled = true) => new()
    {
        DbConnString = "unused-in-tests",
        MoEngageAppId = "MOE-APP",
        MoEngageApiKey = "moe-key",
        MoEngageEndpoint = "https://api-01.moengage.com",
        MoEngageEnabled = true,
        MoEngageAttributesEnabled = attributesEnabled,
    };

    private static DeliveryItem Item(string? userId, string contextJson = "{}") => new(
        AppId: "zainmart", EventRef: 1, ReceivedAt: DateTime.UtcNow, Destination: "moengage_customer", Attempts: 0,
        EventId: Guid.NewGuid(), EventName: "ep_attributes_synced", Origin: "server",
        OccurredAt: DateTime.UtcNow, UserId: userId, AnonymousId: null, SessionKey: null,
        PropertiesJson: "{}", ContextJson: contextJson, Identity: null);

    // ------------------------------------------------------------- skips

    [Fact]
    public async Task Skips_when_user_id_absent()
    {
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item(userId: null), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_user_id", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Skips_with_attributes_disabled_when_the_flag_is_off()
    {
        // SPEC §12 documents `skipped: attributes_disabled`. The sender is
        // registered regardless of the flag precisely so this reason can be
        // reached: the worker runs one pipeline per registered sender, so
        // dropping registration would leave rows enqueued before the flag
        // flipped stuck in `pending` forever rather than reaching a terminal
        // state.
        await Db.Exec(_ds,
            "INSERT INTO user_attributes (app_id, user_id, attributes, hash) VALUES ('zainmart', 'u-off', '{\"city\": \"Baghdad\"}'::jsonb, 'abc')");
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(attributesEnabled: false), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-off"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("attributes_disabled", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Skips_when_user_attributes_row_does_not_exist()
    {
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-missing"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_attributes", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Skips_when_stored_attributes_object_is_empty()
    {
        await Db.Exec(_ds, "INSERT INTO user_attributes (app_id, user_id, attributes, hash) VALUES ('zainmart', 'u-empty', '{}'::jsonb, 'abc')");
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-empty"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_attributes", result.Detail);
        Assert.Empty(stub.Requests);
    }

    // -------------------------------- per-destination customer id (SPEC v1.2)

    [Fact]
    public async Task Uses_moengage_customer_id_from_context_when_stamped_at_enqueue()
    {
        // Fix for the two-profile problem: the reserved event has no
        // session_key, so the enqueue path (EventStore.EnqueueAttributesSyncAsync)
        // stashes the MoEngage-specific id on the outbox row's context.
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-mapped",
            """{"email":"m@example.com"}""", default);
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(
            Item("u-mapped", contextJson: """{"moengage_customer_id":"M-42"}"""),
            default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (_, body) = Assert.Single(stub.Requests);
        using var payload = JsonDocument.Parse(body);
        // customer_id on the wire is MoEngage's id, NOT the app's user_id.
        Assert.Equal("M-42", payload.RootElement.GetProperty("customer_id").GetString());
    }

    [Fact]
    public async Task Falls_back_to_user_id_when_context_has_no_moengage_customer_id()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-fallback",
            """{"email":"f@example.com"}""", default);
        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-fallback"), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (_, body) = Assert.Single(stub.Requests);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("u-fallback", payload.RootElement.GetProperty("customer_id").GetString());
    }

    // ------------------------------------------------------ happy path

    [Fact]
    public async Task Sends_type_customer_with_mapped_attributes_and_writes_back_captured_hash()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-happy",
            """{"first_name":"Ali","email":"ali@example.com","phone":"+9647701234567","gender":"male","city":"Baghdad"}""",
            default);
        var storedHash = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-happy'");

        var stub = Ok();
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-happy"), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);

        var (request, body) = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api-01.moengage.com/v1/customer/MOE-APP", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);

        using var payload = JsonDocument.Parse(body);
        Assert.Equal("customer", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("u-happy", payload.RootElement.GetProperty("customer_id").GetString());
        var attrs = payload.RootElement.GetProperty("attributes");
        Assert.Equal("Ali", attrs.GetProperty("first_name").GetString());
        Assert.Equal("ali@example.com", attrs.GetProperty("email").GetString());
        // SPEC §6.1 mapping: our canonical `phone` → MoEngage's `mobile`
        Assert.Equal("+9647701234567", attrs.GetProperty("mobile").GetString());
        Assert.False(attrs.TryGetProperty("phone", out _));
        Assert.Equal("male", attrs.GetProperty("gender").GetString());
        Assert.Equal("Baghdad", attrs.GetProperty("city").GetString());

        // Write-back: moengage_synced_hash equals what we captured (i.e. the row's hash at fetch time)
        Assert.Equal(storedHash, await Db.Scalar<string>(_ds,
            "SELECT moengage_synced_hash FROM user_attributes WHERE user_id = 'u-happy'"));

        // Regression guard (caught by CI smoke first): the default Utf8JsonWriter
        // encoder escapes `+` as a Unicode escape sequence on the wire. That's
        // still valid JSON but uglier and broke a substring assertion in smoke.
        // All senders now use UnsafeRelaxedJsonEscaping via SenderUtil.WriteJson;
        // the raw request body must contain the literal `+` for E.164 phone
        // numbers (not the escape sequence).
        Assert.Contains(@"""mobile"":""+9647701234567""", body);
        Assert.DoesNotContain("\\u002B", body);
    }

    [Fact]
    public async Task Write_back_uses_hash_at_fetch_even_when_row_changes_mid_flight()
    {
        // Simulate the SPEC §6.1 race: sender captures attrs+hash, then a concurrent
        // setUserAttributes updates the row before the write-back completes. The
        // sender must write the captured hash, not re-read from the row.
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-race",
            """{"email":"a@b.co"}""", default);
        var hashBefore = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-race'");

        // The stub races a concurrent upsert into the same row while the "HTTP" call runs.
        HttpMessageHandler racing = new StubHandler(_ =>
        {
            EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-race", """{"phone":"+9647701234567"}""", default).Wait();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, racing);
        var result = await sender.SendAsync(Item("u-race"), default);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);

        var syncedHash = await Db.Scalar<string>(_ds,
            "SELECT moengage_synced_hash FROM user_attributes WHERE user_id = 'u-race'");
        var currentHash = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-race'");

        Assert.Equal(hashBefore, syncedHash);            // wrote back the captured hash
        Assert.NotEqual(hashBefore, currentHash);        // row moved on mid-flight
        Assert.NotEqual(syncedHash, currentHash);        // sweep / next upsert re-enqueues correctly
    }

    // ---------------------------------------------- failure classification

    [Fact]
    public async Task Retries_on_429_and_5xx_and_no_write_back()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-retry",
            """{"email":"a@b.co"}""", default);

        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway })
        {
            var stub = new StubHandler(_ => new HttpResponseMessage(status));
            var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

            var result = await sender.SendAsync(Item("u-retry"), default);

            Assert.Equal(SendOutcome.Retry, result.Outcome);
            Assert.Contains(((int)status).ToString(), result.Detail);
        }
        // never wrote back a synced hash for a failed send
        Assert.Null(await Db.Scalar<object>(_ds,
            "SELECT moengage_synced_hash FROM user_attributes WHERE user_id = 'u-retry'") as string);
    }

    [Fact]
    public async Task Client_4xx_is_dead_and_no_write_back()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "zainmart", "u-dead",
            """{"email":"a@b.co"}""", default);

        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":"fail"}""", Encoding.UTF8, "application/json"),
        });
        var sender = new MoEngageCustomerSender(TenantFactory.From(Config(), EmptyPlan), TenantFactory.TimeoutMs, _ds, stub);

        var result = await sender.SendAsync(Item("u-dead"), default);

        Assert.Equal(SendOutcome.Dead, result.Outcome);
        Assert.Null(await Db.Scalar<object>(_ds,
            "SELECT moengage_synced_hash FROM user_attributes WHERE user_id = 'u-dead'") as string);
    }
}
