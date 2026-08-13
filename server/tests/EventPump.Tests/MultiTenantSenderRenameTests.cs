using System.Net;
using System.Text.Json;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// SPEC v1.2 §6.2 + §11: each tenant carries its own tracking plan, so two
/// tenants routing the same canonical event to the same destination must be
/// able to rename it differently on the wire. Proves the sender/tenant
/// binding without any live destination — captures outbound payloads with
/// a stub HTTP handler and asserts each tenant's rename applied.
/// </summary>
public class MultiTenantSenderRenameTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid SessionKey = Guid.Parse("018f4d5e-7b20-7abc-8def-0123456789ab");
    private static readonly Guid AnonymousId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly DateTime OccurredAt = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Requests.Add((r, body));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>
    /// Same event name (`order_placed`) with a different per-destination
    /// rename map per tenant. Zainmart maps it to GA4 `purchase` with
    /// `order_id` → `transaction_id` / `revenue` → `value`; Acme maps it to
    /// `checkout_complete` with `order_id` → `order_ref` / `revenue` → `amount`.
    /// The `sku` property is deliberately absent from both rename maps to
    /// prove the allowlist drops unlisted keys (R1/R3).
    /// </summary>
    private static TenantConfig BuildTenant(string appId, string ga4EventName, string orderIdKey, string revenueKey)
    {
        var plan = TrackingPlan.Parse(
            $$"""
            {
              "events": {
                "order_placed": {
                  "origin": "server",
                  "destinations": ["ga4", "amplitude"],
                  "properties": ["order_id", "revenue", "sku"]
                }
              },
              "destinations": {
                "ga4": {
                  "events": {
                    "order_placed": {
                      "name": "{{ga4EventName}}",
                      "properties": { "order_id": "{{orderIdKey}}", "revenue": "{{revenueKey}}" }
                    }
                  }
                },
                "amplitude": {
                  "events": {
                    "order_placed": {
                      "name": "{{ga4EventName}}",
                      "properties": { "order_id": "{{orderIdKey}}", "revenue": "{{revenueKey}}" }
                    }
                  }
                }
              }
            }
            """);

        return new TenantConfig
        {
            AppId = appId,
            TenantApiKey = $"{appId}-tok",
            Plan = plan,
            Ga4Enabled = true,
            Ga4Endpoint = "https://ga4.stub",
            Ga4ApiSecret = "stub",
            Ga4MeasurementId = $"G-{appId.ToUpperInvariant()}",
            AmplitudeEnabled = true,
            AmplitudeEndpoint = "https://amp.stub/2/httpapi",
            AmplitudeApiKey = $"amp-{appId}",
        };
    }

    private static IdentitySnapshot Identity() => new(
        AnonymousId: AnonymousId,
        UserId: "u-42",
        SessionNumber: 1,
        Ga4ClientId: "c1",
        Ga4SessionId: "s1",
        FirebaseAppInstanceId: null,
        AmplitudeDeviceId: "d1",
        AdjustAdid: null,
        AdjustPlatformAdId: null,
        Fbp: null, Fbc: null, ClickIdsJson: "{}",
        ContextJson: "{}",
        ClientIp: null);

    private static DeliveryItem Item(string appId, string destination) => new(
        AppId: appId, EventRef: 1, ReceivedAt: DateTime.UtcNow, Destination: destination, Attempts: 0,
        EventId: EventId, EventName: "order_placed", Origin: "server", OccurredAt: OccurredAt,
        UserId: "u-42", AnonymousId: AnonymousId, SessionKey: SessionKey,
        PropertiesJson: """{"order_id":"o-1","revenue":9.99,"sku":"A1"}""",
        ContextJson: "{}", Identity: Identity());

    [Fact]
    public async Task Ga4_applies_each_tenants_rename_map_independently()
    {
        var zm = BuildTenant("zainmart", "purchase", "transaction_id", "value");
        var acme = BuildTenant("acme", "checkout_complete", "order_ref", "amount");

        var zmStub = new StubHandler();
        var acmeStub = new StubHandler();

        await new Ga4Sender(zm, 10_000, handler: zmStub)
            .SendAsync(Item("zainmart", "ga4"), default);
        await new Ga4Sender(acme, 10_000, handler: acmeStub)
            .SendAsync(Item("acme", "ga4"), default);

        var zmParams = Ga4Params(zmStub.Requests.Single().Body);
        var acmeParams = Ga4Params(acmeStub.Requests.Single().Body);

        // Event name renamed per tenant
        Assert.Equal("purchase", Ga4EventName(zmStub.Requests[0].Body));
        Assert.Equal("checkout_complete", Ga4EventName(acmeStub.Requests[0].Body));

        // Property keys renamed per tenant
        Assert.Equal("o-1", zmParams.GetProperty("transaction_id").GetString());
        Assert.Equal(9.99, zmParams.GetProperty("value").GetDouble());
        Assert.Equal("o-1", acmeParams.GetProperty("order_ref").GetString());
        Assert.Equal(9.99, acmeParams.GetProperty("amount").GetDouble());

        // Allowlist: unlisted `sku` dropped for both tenants
        Assert.False(zmParams.TryGetProperty("sku", out _));
        Assert.False(acmeParams.TryGetProperty("sku", out _));

        // Cross-tenant leakage guard: acme's map keys must not appear in zainmart's payload
        Assert.False(zmParams.TryGetProperty("order_ref", out _));
        Assert.False(zmParams.TryGetProperty("amount", out _));
        Assert.False(acmeParams.TryGetProperty("transaction_id", out _));
        Assert.False(acmeParams.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task Amplitude_applies_each_tenants_rename_map_independently()
    {
        var zm = BuildTenant("zainmart", "Order Placed", "transaction_id", "value");
        var acme = BuildTenant("acme", "Checkout Complete", "order_ref", "amount");

        var zmStub = new StubHandler();
        var acmeStub = new StubHandler();

        await new AmplitudeSender(zm, 10_000, handler: zmStub)
            .SendAsync(Item("zainmart", "amplitude"), default);
        await new AmplitudeSender(acme, 10_000, handler: acmeStub)
            .SendAsync(Item("acme", "amplitude"), default);

        var zmEvent = AmplitudeEvent(zmStub.Requests.Single().Body);
        var acmeEvent = AmplitudeEvent(acmeStub.Requests.Single().Body);

        Assert.Equal("Order Placed", zmEvent.GetProperty("event_type").GetString());
        Assert.Equal("Checkout Complete", acmeEvent.GetProperty("event_type").GetString());

        var zmProps = zmEvent.GetProperty("event_properties");
        var acmeProps = acmeEvent.GetProperty("event_properties");
        Assert.Equal("o-1", zmProps.GetProperty("transaction_id").GetString());
        Assert.Equal(9.99, zmProps.GetProperty("value").GetDouble());
        Assert.Equal("o-1", acmeProps.GetProperty("order_ref").GetString());
        Assert.Equal(9.99, acmeProps.GetProperty("amount").GetDouble());

        // Allowlist drops `sku` for both; cross-tenant keys don't leak.
        Assert.False(zmProps.TryGetProperty("sku", out _));
        Assert.False(acmeProps.TryGetProperty("sku", out _));
        Assert.False(zmProps.TryGetProperty("order_ref", out _));
        Assert.False(acmeProps.TryGetProperty("transaction_id", out _));
    }

    private static JsonElement Ga4Params(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("events")[0].GetProperty("params").Clone();
    }

    private static string Ga4EventName(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("events")[0].GetProperty("name").GetString()!;
    }

    private static JsonElement AmplitudeEvent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("events")[0].Clone();
    }
}
