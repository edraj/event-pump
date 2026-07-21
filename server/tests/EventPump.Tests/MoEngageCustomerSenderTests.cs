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

    private static EpConfig Config() => new()
    {
        DbConnString = "unused-in-tests",
        MoEngageAppId = "MOE-APP",
        MoEngageApiKey = "moe-key",
        MoEngageEndpoint = "https://api-01.moengage.com",
        MoEngageEnabled = true,
        MoEngageAttributesEnabled = true,
    };

    private static DeliveryItem Item(string? userId) => new(
        EventRef: 1, ReceivedAt: DateTime.UtcNow, Destination: "moengage_customer", Attempts: 0,
        EventId: Guid.NewGuid(), EventName: "ep_attributes_synced", Origin: "server",
        OccurredAt: DateTime.UtcNow, UserId: userId, AnonymousId: null, SessionKey: null,
        PropertiesJson: "{}", ContextJson: "{}", Identity: null);

    // ------------------------------------------------------------- skips

    [Fact]
    public async Task Skips_when_user_id_absent()
    {
        var stub = Ok();
        var sender = new MoEngageCustomerSender(Config(), _ds, stub);

        var result = await sender.SendAsync(Item(userId: null), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_user_id", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Skips_when_user_attributes_row_does_not_exist()
    {
        var stub = Ok();
        var sender = new MoEngageCustomerSender(Config(), _ds, stub);

        var result = await sender.SendAsync(Item("u-missing"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_attributes", result.Detail);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Skips_when_stored_attributes_object_is_empty()
    {
        await Db.Exec(_ds, "INSERT INTO user_attributes (user_id, attributes, hash) VALUES ('u-empty', '{}'::jsonb, 'abc')");
        var stub = Ok();
        var sender = new MoEngageCustomerSender(Config(), _ds, stub);

        var result = await sender.SendAsync(Item("u-empty"), default);

        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_attributes", result.Detail);
        Assert.Empty(stub.Requests);
    }

    // ------------------------------------------------------ happy path

    [Fact]
    public async Task Sends_type_customer_with_mapped_attributes_and_writes_back_captured_hash()
    {
        await EventStore.UpsertUserAttributesAsync(_ds, "u-happy",
            """{"first_name":"Ali","email":"ali@example.com","phone":"+9647701234567","gender":"male","city":"Baghdad"}""",
            default);
        var storedHash = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-happy'");

        var stub = Ok();
        var sender = new MoEngageCustomerSender(Config(), _ds, stub);

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
        await EventStore.UpsertUserAttributesAsync(_ds, "u-race",
            """{"email":"a@b.co"}""", default);
        var hashBefore = await Db.Scalar<string>(_ds,
            "SELECT hash FROM user_attributes WHERE user_id = 'u-race'");

        // The stub races a concurrent upsert into the same row while the "HTTP" call runs.
        HttpMessageHandler racing = new StubHandler(_ =>
        {
            EventStore.UpsertUserAttributesAsync(_ds, "u-race", """{"phone":"+9647701234567"}""", default).Wait();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var sender = new MoEngageCustomerSender(Config(), _ds, racing);
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
        await EventStore.UpsertUserAttributesAsync(_ds, "u-retry",
            """{"email":"a@b.co"}""", default);

        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway })
        {
            var stub = new StubHandler(_ => new HttpResponseMessage(status));
            var sender = new MoEngageCustomerSender(Config(), _ds, stub);

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
        await EventStore.UpsertUserAttributesAsync(_ds, "u-dead",
            """{"email":"a@b.co"}""", default);

        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":"fail"}""", Encoding.UTF8, "application/json"),
        });
        var sender = new MoEngageCustomerSender(Config(), _ds, stub);

        var result = await sender.SendAsync(Item("u-dead"), default);

        Assert.Equal(SendOutcome.Dead, result.Outcome);
        Assert.Null(await Db.Scalar<object>(_ds,
            "SELECT moengage_synced_hash FROM user_attributes WHERE user_id = 'u-dead'") as string);
    }
}
