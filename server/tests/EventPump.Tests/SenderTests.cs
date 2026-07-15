using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

public class SenderTests
{
    private static readonly DateTime OccurredAt = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid EventId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    // UUIDv7 session key: first 48 bits are the session-start unix ms
    private static readonly Guid SessionKey = Guid.Parse("018f4d5e-7b20-7abc-8def-0123456789ab");
    private static readonly long SessionStartMs = Convert.ToInt64("018f4d5e7b20", 16);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return responder(request);
        }
    }

    private static StubHandler Respond(HttpStatusCode status, string body = "{}")
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private static IdentitySnapshot Identity(
        string? ga4ClientId = "123456789.1700000000",
        string? ga4SessionId = "1699999999",
        string? firebaseAppInstanceId = null,
        string? amplitudeDeviceId = "0f2937de-92f9-4b6c-a222-abcdefabcdef",
        string? adjustAdid = "adid-9",
        string? adjustPlatformAdId = null,
        string? userId = "u-42",
        string contextJson =
            """{"language":"ar","screen_resolution":"1920x1080","os":"Android","os_version":"14","model":"Pixel 8","category":"mobile","user_agent":"Mozilla/5.0 Test","app_version":"2.3.4"}""")
        => new(
            AnonymousId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            UserId: userId,
            SessionNumber: 3,
            Ga4ClientId: ga4ClientId,
            Ga4SessionId: ga4SessionId,
            FirebaseAppInstanceId: firebaseAppInstanceId,
            AmplitudeDeviceId: amplitudeDeviceId,
            AdjustAdid: adjustAdid,
            AdjustPlatformAdId: adjustPlatformAdId,
            Fbp: "fb.1.1700000000.def",
            Fbc: "fb.1.1700000000.abc",
            ClickIdsJson: "{}",
            ContextJson: contextJson,
            ClientIp: "203.0.113.9");

    private static DeliveryItem Item(
        string destination,
        IdentitySnapshot? identity,
        string eventName = "order_placed",
        string propertiesJson = """{"sku":"A1","revenue":10.5,"currency":"IQD","order_id":"o-1"}""",
        string contextJson = """{"page":{"path":"/checkout"},"engagement_time_msec":1200,"session_number":3}""",
        string? userId = "u-42")
        => new(1, DateTime.UtcNow, destination, 0, EventId, eventName, "server", OccurredAt,
            userId, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), SessionKey,
            propertiesJson, contextJson, identity);

    private static EpConfig Config() => new()
    {
        DbConnString = "unused",
        Ga4ApiSecret = "ga4-secret",
        Ga4MeasurementId = "G-TEST1",
        Ga4FirebaseAppId = "1:123:android:abc",
        AmplitudeApiKey = "amp-key",
        MoEngageAppId = "MOE-APP",
        MoEngageApiKey = "moe-key",
        AdjustAppToken = "app-tok",
        AdjustS2sToken = "s2s-secret",
        MetaPixelId = "pixel-1",
        MetaAccessToken = "meta-tok",
        MetaTestEventCode = "TEST99",
    };

    private static TrackingPlan Plan() => TrackingPlan.Parse(
        """
        {"events":{"order_placed":{"origin":"server","destinations":["ga4","amplitude","moengage","adjust","meta"],
                    "meta_name":"Purchase","adjust_token":"abc123"}}}
        """);

    // ------------------------------------------------------------------ GA4

    [Fact]
    public async Task Ga4_builds_measurement_id_payload()
    {
        var stub = Respond(HttpStatusCode.NoContent, "");
        var sender = new Ga4Sender(Config(), handler: stub);

        var result = await sender.SendAsync(Item("ga4", Identity()), CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = stub.Requests.Single();
        Assert.Contains("/mp/collect", request.RequestUri!.AbsolutePath);
        Assert.Contains("api_secret=ga4-secret", request.RequestUri.Query);
        Assert.Contains("measurement_id=G-TEST1", request.RequestUri.Query);
        Assert.DoesNotContain("firebase_app_id", request.RequestUri.Query);

        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        Assert.Equal("123456789.1700000000", root.GetProperty("client_id").GetString());
        Assert.Equal("u-42", root.GetProperty("user_id").GetString());
        Assert.Equal(((DateTimeOffset)OccurredAt).ToUnixTimeMilliseconds() * 1000,
            root.GetProperty("timestamp_micros").GetInt64());
        Assert.Equal("203.0.113.9", root.GetProperty("ip_override").GetString());
        var device = root.GetProperty("device");
        Assert.Equal("mobile", device.GetProperty("category").GetString());
        Assert.Equal("ar", device.GetProperty("language").GetString());
        Assert.Equal("1920x1080", device.GetProperty("screen_resolution").GetString());
        Assert.Equal("Android", device.GetProperty("operating_system").GetString());
        Assert.Equal("14", device.GetProperty("operating_system_version").GetString());
        Assert.Equal("Pixel 8", device.GetProperty("model").GetString());
        var ev = root.GetProperty("events")[0];
        Assert.Equal("order_placed", ev.GetProperty("name").GetString());
        var parameters = ev.GetProperty("params");
        Assert.Equal("1699999999", parameters.GetProperty("session_id").GetString());
        Assert.Equal(1200, parameters.GetProperty("engagement_time_msec").GetInt32());
        Assert.Equal("A1", parameters.GetProperty("sku").GetString());
        Assert.Equal(10.5, parameters.GetProperty("revenue").GetDouble());
    }

    [Fact]
    public async Task Ga4_uses_firebase_app_id_when_no_client_id()
    {
        var stub = Respond(HttpStatusCode.NoContent, "");
        var sender = new Ga4Sender(Config(), handler: stub);

        var identity = Identity(ga4ClientId: null, ga4SessionId: null,
            firebaseAppInstanceId: "fiid-123");
        var result = await sender.SendAsync(Item("ga4", identity), CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = stub.Requests.Single();
        Assert.Contains("firebase_app_id=", request.RequestUri!.Query);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("fiid-123", payload.RootElement.GetProperty("app_instance_id").GetString());
        Assert.False(payload.RootElement.TryGetProperty("client_id", out _));
    }

    [Fact]
    public async Task Ga4_skips_without_identity_and_never_fabricates()
    {
        var sender = new Ga4Sender(Config(), handler: Respond(HttpStatusCode.NoContent, ""));

        var noRegistry = await sender.SendAsync(Item("ga4", null), CancellationToken.None);
        var noIds = await sender.SendAsync(
            Item("ga4", Identity(ga4ClientId: null, ga4SessionId: null)), CancellationToken.None);

        Assert.Equal(SendOutcome.Skip, noRegistry.Outcome);
        Assert.Equal("no_ga4_identity", noRegistry.Detail);
        Assert.Equal(SendOutcome.Skip, noIds.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, SendOutcome.Retry)]
    public async Task Ga4_maps_status_codes(HttpStatusCode status, SendOutcome expected)
    {
        var sender = new Ga4Sender(Config(), handler: Respond(status, ""));
        var result = await sender.SendAsync(Item("ga4", Identity()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    // ------------------------------------------------------------ Amplitude

    [Fact]
    public async Task Amplitude_builds_v2_payload_with_insert_id_dedupe()
    {
        var stub = Respond(HttpStatusCode.OK, """{"code":200,"events_ingested":1}""");
        var sender = new AmplitudeSender(Config(), handler: stub);

        var result = await sender.SendAsync(Item("amplitude", Identity()), CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (_, body) = stub.Requests.Single();
        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        Assert.Equal("amp-key", root.GetProperty("api_key").GetString());
        var ev = root.GetProperty("events")[0];
        Assert.Equal("order_placed", ev.GetProperty("event_type").GetString());
        Assert.Equal(EventId.ToString(), ev.GetProperty("insert_id").GetString());
        Assert.Equal("0f2937de-92f9-4b6c-a222-abcdefabcdef", ev.GetProperty("device_id").GetString());
        Assert.Equal("u-42", ev.GetProperty("user_id").GetString());
        Assert.Equal(((DateTimeOffset)OccurredAt).ToUnixTimeMilliseconds(), ev.GetProperty("time").GetInt64());
        Assert.Equal(SessionStartMs, ev.GetProperty("session_id").GetInt64());
        Assert.Equal("Android", ev.GetProperty("os_name").GetString());
        Assert.Equal("14", ev.GetProperty("os_version").GetString());
        Assert.Equal("Pixel 8", ev.GetProperty("device_model").GetString());
        Assert.Equal("ar", ev.GetProperty("language").GetString());
        Assert.Equal("2.3.4", ev.GetProperty("app_version").GetString());
        Assert.Equal("203.0.113.9", ev.GetProperty("ip").GetString());
        Assert.Equal("A1", ev.GetProperty("event_properties").GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Amplitude_skips_without_device_id()
    {
        var sender = new AmplitudeSender(Config(), handler: Respond(HttpStatusCode.OK));
        var result = await sender.SendAsync(
            Item("amplitude", Identity(amplitudeDeviceId: null)), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_amplitude_device_id", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.Forbidden, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.BadGateway, SendOutcome.Retry)]
    public async Task Amplitude_maps_status_codes(HttpStatusCode status, SendOutcome expected)
    {
        var sender = new AmplitudeSender(Config(), handler: Respond(status));
        var result = await sender.SendAsync(Item("amplitude", Identity()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    // ------------------------------------------------------------- MoEngage

    [Fact]
    public async Task Moengage_builds_event_payload_with_basic_auth()
    {
        var stub = Respond(HttpStatusCode.OK, """{"status":"success"}""");
        var sender = new MoEngageSender(Config(), stub);

        var result = await sender.SendAsync(Item("moengage", Identity()), CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = stub.Requests.Single();
        Assert.EndsWith("/v1/event/MOE-APP", request.RequestUri!.AbsolutePath);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("MOE-APP:moe-key")),
            request.Headers.Authorization.Parameter);

        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        Assert.Equal("event", root.GetProperty("type").GetString());
        Assert.Equal("u-42", root.GetProperty("customer_id").GetString());
        var action = root.GetProperty("actions")[0];
        Assert.Equal("order_placed", action.GetProperty("action").GetString());
        Assert.Equal("ANDROID", action.GetProperty("platform").GetString());
        Assert.Equal(((DateTimeOffset)OccurredAt).ToUnixTimeSeconds(), action.GetProperty("current_time").GetInt64());
        Assert.Equal("A1", action.GetProperty("attributes").GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Moengage_skips_without_user_id()
    {
        var sender = new MoEngageSender(Config(), Respond(HttpStatusCode.OK));
        var result = await sender.SendAsync(
            Item("moengage", Identity(userId: null), userId: null), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_user_id", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.TooManyRequests, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, SendOutcome.Retry)]
    public async Task Moengage_maps_status_codes(HttpStatusCode status, SendOutcome expected)
    {
        var sender = new MoEngageSender(Config(), Respond(status, """{"status":"fail"}"""));
        var result = await sender.SendAsync(Item("moengage", Identity()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    // --------------------------------------------------------------- Adjust

    [Fact]
    public async Task Adjust_builds_form_encoded_s2s_request()
    {
        var stub = Respond(HttpStatusCode.OK, "OK");
        var sender = new AdjustSender(Config(), Plan(), handler: stub);

        var result = await sender.SendAsync(Item("adjust", Identity()), CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = stub.Requests.Single();
        Assert.Equal("application/x-www-form-urlencoded",
            request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("s2s-secret", request.Headers.Authorization.Parameter);

        var form = body.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("1", form["s2s"]);
        Assert.Equal("app-tok", form["app_token"]);
        Assert.Equal("abc123", form["event_token"]);
        Assert.Equal("adid-9", form["adid"]);
        Assert.Equal(((DateTimeOffset)OccurredAt).ToUnixTimeSeconds().ToString(), form["created_at_unix"]);
        Assert.Equal("10.5", form["revenue"]);
        Assert.Equal("IQD", form["currency"]);
        Assert.Equal("203.0.113.9", form["ip_address"]);
        Assert.Equal("Mozilla/5.0 Test", form["user_agent"]);
    }

    [Fact]
    public async Task Adjust_skips_without_device_or_event_token()
    {
        var sender = new AdjustSender(Config(), Plan(), handler: Respond(HttpStatusCode.OK, "OK"));

        var noDevice = await sender.SendAsync(
            Item("adjust", Identity(adjustAdid: null)), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, noDevice.Outcome);
        Assert.Equal("no_adjust_adid", noDevice.Detail);

        var noToken = await sender.SendAsync(
            Item("adjust", Identity(), eventName: "unmapped_event"), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, noToken.Outcome);
        Assert.Equal("no_event_token", noToken.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.Forbidden, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.NotFound, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, SendOutcome.Retry)]
    public async Task Adjust_maps_status_codes(HttpStatusCode status, SendOutcome expected)
    {
        var sender = new AdjustSender(Config(), Plan(), handler: Respond(status, """{"error":"x"}"""));
        var result = await sender.SendAsync(Item("adjust", Identity()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    // ----------------------------------------------------------------- Meta

    [Fact]
    public async Task Meta_builds_capi_payload_with_translated_name_and_hashes()
    {
        var stub = Respond(HttpStatusCode.OK, """{"events_received":1}""");
        var sender = new MetaCapiSender(Config(), Plan(), stub);

        var item = Item("meta", Identity(),
            propertiesJson: """{"email":"User@Example.com","phone":"+964 770 123 4567","revenue":10.5,"currency":"IQD","order_id":"o-1"}""");
        var result = await sender.SendAsync(item, CancellationToken.None);

        Assert.Equal(SendOutcome.Delivered, result.Outcome);
        var (request, body) = stub.Requests.Single();
        Assert.Contains("/v25.0/pixel-1/events", request.RequestUri!.AbsolutePath);
        Assert.Contains("access_token=meta-tok", request.RequestUri.Query);

        using var payload = JsonDocument.Parse(body);
        var root = payload.RootElement;
        Assert.Equal("TEST99", root.GetProperty("test_event_code").GetString());
        var data = root.GetProperty("data")[0];
        Assert.Equal("Purchase", data.GetProperty("event_name").GetString());
        Assert.Equal(((DateTimeOffset)OccurredAt).ToUnixTimeSeconds(), data.GetProperty("event_time").GetInt64());
        Assert.Equal(EventId.ToString(), data.GetProperty("event_id").GetString());
        Assert.Equal("website", data.GetProperty("action_source").GetString());
        var userData = data.GetProperty("user_data");
        Assert.Equal(PixelPlatformSender.NormalizeEmail("user@example.com"),
            userData.GetProperty("em").GetString());
        Assert.Equal(PixelPlatformSender.NormalizePhone("9647701234567"),
            userData.GetProperty("ph").GetString());
        Assert.Equal(PixelPlatformSender.Sha256Lower("u-42"),
            userData.GetProperty("external_id").GetString());
        Assert.Equal("fb.1.1700000000.def", userData.GetProperty("fbp").GetString());
        Assert.Equal("fb.1.1700000000.abc", userData.GetProperty("fbc").GetString());
        Assert.Equal("203.0.113.9", userData.GetProperty("client_ip_address").GetString());
        Assert.Equal("Mozilla/5.0 Test", userData.GetProperty("client_user_agent").GetString());
        var customData = data.GetProperty("custom_data");
        Assert.Equal(10.5, customData.GetProperty("value").GetDouble());
        Assert.Equal("IQD", customData.GetProperty("currency").GetString());
        Assert.Equal("o-1", customData.GetProperty("order_id").GetString());
        // raw PII never leaves the process
        Assert.DoesNotContain("User@Example.com", body);
        Assert.DoesNotContain("9647701234567", body);
    }

    [Fact]
    public async Task Meta_skips_without_any_user_data()
    {
        var sender = new MetaCapiSender(Config(), Plan(), Respond(HttpStatusCode.OK));
        var result = await sender.SendAsync(
            Item("meta", null, propertiesJson: "{}", userId: null), CancellationToken.None);
        Assert.Equal(SendOutcome.Skip, result.Outcome);
        Assert.Equal("no_user_data", result.Detail);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 190, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.BadRequest, 100, SendOutcome.Dead)]
    [InlineData(HttpStatusCode.BadRequest, 4, SendOutcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, 2, SendOutcome.Retry)]
    public async Task Meta_maps_error_codes(HttpStatusCode status, int code, SendOutcome expected)
    {
        var body = "{\"error\":{\"message\":\"x\",\"type\":\"OAuthException\",\"code\":" + code + "}}";
        var sender = new MetaCapiSender(Config(), Plan(), Respond(status, body));
        var result = await sender.SendAsync(Item("meta", Identity()), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }
}
