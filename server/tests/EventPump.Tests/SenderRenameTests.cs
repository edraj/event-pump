using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Exercises SPEC §6.2 per-destination event-name and property-key renames
/// end-to-end through each sender's payload builder. Uses stub HTTP handlers;
/// no PostgreSQL required.
/// </summary>
public class SenderRenameTests
{
    // -------------------------------------------------------- test helpers

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

    private static IdentitySnapshot Identity() => new(
        AnonymousId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        UserId: "u-42",
        SessionNumber: 3,
        Ga4ClientId: "c1",
        Ga4SessionId: "s1",
        FirebaseAppInstanceId: null,
        AmplitudeDeviceId: "d1",
        AdjustAdid: "adid",
        AdjustPlatformAdId: null,
        Fbp: null, Fbc: null, ClickIdsJson: "{}",
        ContextJson: """{"os":"Android"}""",
        ClientIp: "203.0.113.9");

    private static DeliveryItem Item(string destination, string propsJson) => new(
        1, DateTime.UtcNow, destination, 0, EventId,
        "order_placed", "server", DateTime.UtcNow,
        "u-42", Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"), SessionKey,
        propsJson, "{}", Identity());

    private static EpConfig Config() => new()
    {
        DbConnString = "unused",
        Ga4ApiSecret = "s", Ga4MeasurementId = "G-TEST",
        AmplitudeApiKey = "amp",
        MoEngageAppId = "MOE", MoEngageApiKey = "k",
        AdjustAppToken = "app-tok",
        MetaPixelId = "px", MetaAccessToken = "meta-tok",
    };

    private static TrackingPlan RenamePlan() => TrackingPlan.Parse(
        """
        {
          "events": {
            "order_placed": {
              "origin": "server",
              "destinations": ["ga4", "amplitude", "moengage", "adjust", "meta"],
              "adjust_token": "abc123"
            }
          },
          "destinations": {
            "ga4": {
              "events": {
                "order_placed": {
                  "name": "purchase",
                  "properties": { "order_id": "transaction_id", "revenue": "value", "currency": "currency" }
                }
              }
            },
            "amplitude": {
              "events": {
                "order_placed": {
                  "name": "Order Completed",
                  "properties": { "order_id": "orderId", "revenue": "revenue" }
                }
              }
            },
            "moengage": {
              "events": {
                "order_placed": {
                  "name": "Order Placed",
                  "properties": { "revenue": "amount", "order_id": "order_id" }
                }
              }
            },
            "adjust": {
              "events": {
                "order_placed": { "properties": { "value": "revenue", "currency": "currency" } }
              }
            },
            "meta": {
              "events": {
                "order_placed": { "name": "Purchase" }
              }
            }
          }
        }
        """);

    // ================================================================== GA4

    [Fact]
    public async Task Ga4_uses_renamed_event_name_and_property_keys()
    {
        var plan = RenamePlan();
        var stub = new StubHandler();
        await new Ga4Sender(Config(), plan, handler: stub).SendAsync(
            Item("ga4", """{"order_id":"o-1","revenue":9.99,"currency":"IQD"}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var evt = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("purchase", evt.GetProperty("name").GetString());
        var p = evt.GetProperty("params");
        Assert.Equal("o-1", p.GetProperty("transaction_id").GetString());
        Assert.Equal(9.99, p.GetProperty("value").GetDouble());
        Assert.Equal("IQD", p.GetProperty("currency").GetString());
        Assert.False(p.TryGetProperty("order_id", out _));
        Assert.False(p.TryGetProperty("revenue", out _));
    }

    [Fact]
    public async Task Ga4_drops_properties_not_in_the_allowlist()
    {
        var plan = RenamePlan();
        var stub = new StubHandler();
        // `sku` is not in the ga4 rename map — dropped.
        await new Ga4Sender(Config(), plan, handler: stub).SendAsync(
            Item("ga4", """{"order_id":"o-1","revenue":9.99,"currency":"IQD","sku":"A1"}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var p = doc.RootElement.GetProperty("events")[0].GetProperty("params");
        Assert.False(p.TryGetProperty("sku", out _));
    }

    // ============================================================ Amplitude

    [Fact]
    public async Task Amplitude_uses_renamed_event_type_and_property_keys()
    {
        var plan = RenamePlan();
        var stub = new StubHandler();
        await new AmplitudeSender(Config(), plan, handler: stub).SendAsync(
            Item("amplitude", """{"order_id":"o-1","revenue":9.99}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var evt = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("Order Completed", evt.GetProperty("event_type").GetString());
        var p = evt.GetProperty("event_properties");
        Assert.Equal("o-1", p.GetProperty("orderId").GetString());
        Assert.Equal(9.99, p.GetProperty("revenue").GetDouble()); // identity mapping ("revenue" -> "revenue")
        Assert.False(p.TryGetProperty("order_id", out _));
    }

    // ============================================================= MoEngage

    [Fact]
    public async Task MoEngage_uses_renamed_action_name_and_attribute_keys()
    {
        var plan = RenamePlan();
        var stub = new StubHandler();
        await new MoEngageSender(Config(), plan, handler: stub).SendAsync(
            Item("moengage", """{"order_id":"o-1","revenue":9.99}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var action = doc.RootElement.GetProperty("actions")[0];
        Assert.Equal("Order Placed", action.GetProperty("action").GetString());
        var a = action.GetProperty("attributes");
        Assert.Equal(9.99, a.GetProperty("amount").GetDouble());
        Assert.Equal("o-1", a.GetProperty("order_id").GetString()); // identity mapping
        Assert.False(a.TryGetProperty("revenue", out _));
    }

    // =============================================================== Adjust

    [Fact]
    public async Task Adjust_applies_property_rename_before_revenue_lookup()
    {
        // Canonical uses `value`; rename `value → revenue` lets AdjustSender's
        // existing revenue extractor find it. No `name` rename (R6).
        var plan = RenamePlan();
        var stub = new StubHandler();
        await new AdjustSender(Config(), plan, handler: stub).SendAsync(
            Item("adjust", """{"value":9.99,"currency":"IQD"}"""), default);

        var body = stub.Requests[0].Body;
        var form = new Dictionary<string, string>();
        foreach (var pair in body.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) form[pair[..eq]] = HttpUtility.UrlDecode(pair[(eq + 1)..]);
        }
        Assert.Equal("9.99", form["revenue"]);
        Assert.Equal("IQD", form["currency"]);
        Assert.Equal("abc123", form["event_token"]);
    }

    // ================================================================= Meta

    [Fact]
    public async Task Meta_uses_renamed_event_name()
    {
        var plan = RenamePlan();
        var stub = new StubHandler();
        var config = Config() with { MetaConsentGating = false };
        await new MetaCapiSender(config, plan, stub).SendAsync(
            Item("meta", """{"revenue":9.99,"currency":"IQD","order_id":"o-1"}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var evt = doc.RootElement.GetProperty("data")[0];
        Assert.Equal("Purchase", evt.GetProperty("event_name").GetString());
    }

    // ================================================== R1: no rename block

    [Fact]
    public async Task Ga4_passes_canonical_name_when_no_rename_declared()
    {
        // No `destinations.ga4` block at all — R1 fallback.
        var plan = TrackingPlan.Parse(
            """{"events":{"order_placed":{"origin":"server","destinations":["ga4"]}}}""");
        var stub = new StubHandler();
        await new Ga4Sender(Config(), plan, handler: stub).SendAsync(
            Item("ga4", """{"currency":"IQD"}"""), default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        Assert.Equal("order_placed", doc.RootElement.GetProperty("events")[0].GetProperty("name").GetString());
    }
}
