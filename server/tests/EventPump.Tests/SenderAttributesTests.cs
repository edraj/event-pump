using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using EventPump.Config;
using EventPump.Data;
using EventPump.Senders;
using EventPump.Worker;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Per-sender coverage for SPEC §6.1 attribute payloads on the three
/// event-carrying destinations (GA4/Amplitude/Adjust). MoEngage's dedicated
/// customer sync is exercised by <see cref="MoEngageCustomerSenderTests"/>.
/// </summary>
[Collection("pg")]
public class SenderAttributesTests(PostgresFixture pg) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync() => _ds = await pg.CreateMigratedDatabaseAsync();
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    // ---------------------------------------------------------- helpers

    private sealed class StubHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Requests.Add((r, body));
            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    private static readonly Guid EventId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid SessionKey = Guid.Parse("018f4d5e-7b20-7abc-8def-0123456789ab");
    private static readonly DateTime OccurredAt = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private static IdentitySnapshot Identity(string userId = "u-42") => new(
        AnonymousId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        UserId: userId,
        SessionNumber: 3,
        Ga4ClientId: "123456789.1700000000",
        Ga4SessionId: "1699999999",
        FirebaseAppInstanceId: null,
        AmplitudeDeviceId: "0f2937de-92f9-4b6c-a222-abcdefabcdef",
        AdjustAdid: "adid-9",
        AdjustPlatformAdId: null,
        Fbp: null, Fbc: null, ClickIdsJson: "{}",
        ContextJson: """{"language":"ar","os":"Android","user_agent":"Mozilla/5.0"}""",
        ClientIp: "203.0.113.9");

    private static DeliveryItem Item(string destination, string userId = "u-42") => new(
        1, DateTime.UtcNow, destination, 0, EventId, "order_placed", "server", OccurredAt,
        userId, Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), SessionKey,
        """{"revenue":10.5,"currency":"IQD"}""",
        """{"engagement_time_msec":1200,"session_number":3}""",
        Identity(userId));

    private static EpConfig Config() => new()
    {
        DbConnString = "unused",
        Ga4ApiSecret = "ga4-secret",
        Ga4MeasurementId = "G-TEST1",
        AmplitudeApiKey = "amp-key",
        AdjustAppToken = "app-tok",
    };

    private static string Sha256Hex(string s)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(s), hash);
        return Convert.ToHexStringLower(hash);
    }

    private async Task Seed(string userId = "u-42") => await EventStore.UpsertUserAttributesAsync(
        _ds, userId,
        """{"first_name":"Ali","last_name":"Hassan","email":"ali@example.com","phone":"+9647701234567","gender":"male","city":"Baghdad"}""",
        default);

    private static TrackingPlan Plan() => TrackingPlan.Parse(
        """{"events":{"order_placed":{"origin":"server","destinations":["ga4","amplitude","adjust","meta","moengage"],"adjust_token":"abc123"}}}""");

    private static TrackingPlan PlanWithAdjust() => TrackingPlan.Parse(
        """{"events":{"order_placed":{"origin":"server","destinations":["adjust"],"adjust_token":"abc123"}}}""");

    // ==================================================================
    //                              GA4
    // ==================================================================

    [Fact]
    public async Task Ga4_omits_attribute_fields_when_flag_off()
    {
        await Seed();
        var config = Config() with { Ga4AttributesEnabled = false };
        var stub = new StubHandler();
        await new Ga4Sender(config, Plan(), _ds, stub).SendAsync(Item("ga4"), default);

        using var payload = JsonDocument.Parse(stub.Requests[0].Body);
        Assert.False(payload.RootElement.TryGetProperty("user_properties", out _));
        Assert.False(payload.RootElement.TryGetProperty("user_data", out _));
    }

    [Fact]
    public async Task Ga4_omits_attribute_fields_when_no_user_attributes_row_exists()
    {
        var config = Config() with { Ga4AttributesEnabled = true };
        var stub = new StubHandler();
        await new Ga4Sender(config, Plan(), _ds, stub).SendAsync(Item("ga4"), default);

        using var payload = JsonDocument.Parse(stub.Requests[0].Body);
        Assert.False(payload.RootElement.TryGetProperty("user_properties", out _));
        Assert.False(payload.RootElement.TryGetProperty("user_data", out _));
    }

    [Fact]
    public async Task Ga4_emits_user_properties_and_hashed_user_data_when_enabled()
    {
        await Seed();
        var config = Config() with { Ga4AttributesEnabled = true };
        var stub = new StubHandler();
        await new Ga4Sender(config, Plan(), _ds, stub).SendAsync(Item("ga4"), default);

        using var payload = JsonDocument.Parse(stub.Requests[0].Body);
        var props = payload.RootElement.GetProperty("user_properties");
        Assert.Equal("Ali", props.GetProperty("first_name").GetProperty("value").GetString());
        Assert.Equal("Hassan", props.GetProperty("last_name").GetProperty("value").GetString());
        Assert.Equal("male", props.GetProperty("gender").GetProperty("value").GetString());
        Assert.Equal("Baghdad", props.GetProperty("city").GetProperty("value").GetString());
        // email/phone go via user_data (hashed), never user_properties (raw)
        Assert.False(props.TryGetProperty("email", out _));
        Assert.False(props.TryGetProperty("phone", out _));

        var data = payload.RootElement.GetProperty("user_data");
        Assert.Equal(Sha256Hex("ali@example.com"), data.GetProperty("sha256_email_address").GetString());
        // phone SHA-256 is taken over E.164 with the leading `+` stripped
        Assert.Equal(Sha256Hex("9647701234567"), data.GetProperty("sha256_phone_number").GetString());
    }

    // ==================================================================
    //                            Amplitude
    // ==================================================================

    [Fact]
    public async Task Amplitude_omits_user_properties_when_flag_off()
    {
        await Seed();
        var config = Config() with { AmplitudeAttributesEnabled = false };
        var stub = new StubHandler();
        await new AmplitudeSender(config, Plan(), _ds, stub).SendAsync(Item("amplitude"), default);

        using var payload = JsonDocument.Parse(stub.Requests[0].Body);
        var evt = payload.RootElement.GetProperty("events")[0];
        Assert.False(evt.TryGetProperty("user_properties", out _));
    }

    [Fact]
    public async Task Amplitude_emits_all_six_attributes_raw_when_enabled()
    {
        await Seed();
        var config = Config() with { AmplitudeAttributesEnabled = true };
        var stub = new StubHandler();
        await new AmplitudeSender(config, Plan(), _ds, stub).SendAsync(Item("amplitude"), default);

        using var payload = JsonDocument.Parse(stub.Requests[0].Body);
        var props = payload.RootElement.GetProperty("events")[0].GetProperty("user_properties");
        Assert.Equal("Ali", props.GetProperty("first_name").GetString());
        Assert.Equal("Hassan", props.GetProperty("last_name").GetString());
        Assert.Equal("ali@example.com", props.GetProperty("email").GetString());
        Assert.Equal("+9647701234567", props.GetProperty("phone").GetString());
        Assert.Equal("male", props.GetProperty("gender").GetString());
        Assert.Equal("Baghdad", props.GetProperty("city").GetString());
    }

    // ==================================================================
    //                              Adjust
    // ==================================================================

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in body.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[pair[..eq]] = HttpUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return result;
    }

    [Fact]
    public async Task Adjust_omits_attribute_fields_when_flag_off()
    {
        await Seed();
        var config = Config() with { AdjustAttributesEnabled = false };
        var stub = new StubHandler();
        await new AdjustSender(config, PlanWithAdjust(), _ds, stub).SendAsync(Item("adjust"), default);

        var form = ParseForm(stub.Requests[0].Body);
        Assert.False(form.ContainsKey("s2s_email"));
        Assert.False(form.ContainsKey("s2s_phone"));
        Assert.False(form.ContainsKey("partner_params"));
    }

    [Fact]
    public async Task Adjust_emits_hashed_s2s_identifiers_and_partner_params_when_enabled()
    {
        await Seed();
        var config = Config() with { AdjustAttributesEnabled = true };
        var stub = new StubHandler();
        await new AdjustSender(config, PlanWithAdjust(), _ds, stub).SendAsync(Item("adjust"), default);

        var form = ParseForm(stub.Requests[0].Body);
        Assert.Equal(Sha256Hex("ali@example.com"), form["s2s_email"]);
        // phone SHA-256 without the leading `+` per Adjust convention
        Assert.Equal(Sha256Hex("9647701234567"), form["s2s_phone"]);

        using var partner = JsonDocument.Parse(form["partner_params"]);
        Assert.Equal("Ali", partner.RootElement.GetProperty("first_name").GetString());
        Assert.Equal("Hassan", partner.RootElement.GetProperty("last_name").GetString());
        Assert.Equal("male", partner.RootElement.GetProperty("gender").GetString());
        Assert.Equal("Baghdad", partner.RootElement.GetProperty("city").GetString());
        // email/phone go via s2s_*, never partner_params
        Assert.False(partner.RootElement.TryGetProperty("email", out _));
        Assert.False(partner.RootElement.TryGetProperty("phone", out _));
    }
}
