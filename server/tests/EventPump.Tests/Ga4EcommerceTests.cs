using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Senders;
using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Phase 2 coverage for GA4 e-commerce shape (SPEC §12): verifies the
/// nested `items[]` array is built from canonical properties for single-
/// and multi-product events, with the fixed item field mapping
/// (product_id→item_id, product_name→item_name, etc.) documented in
/// Ga4EcommerceTransform.
/// </summary>
public class Ga4EcommerceTests
{
    // ------------------------------------------------------------ helpers

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

    private static readonly Guid EventId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid SessionKey = Guid.Parse("018f4d5e-7b20-7abc-8def-0123456789ab");

    private static IdentitySnapshot Identity() => new(
        AnonymousId: Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        UserId: "u-42",
        SessionNumber: 3,
        Ga4ClientId: "cid", Ga4SessionId: "s1",
        FirebaseAppInstanceId: null,
        AmplitudeDeviceId: null,
        AdjustAdid: null, AdjustPlatformAdId: null,
        Fbp: null, Fbc: null, ClickIdsJson: "{}",
        ContextJson: """{"os":"Android"}""",
        ClientIp: "203.0.113.9");

    private static DeliveryItem Item(string eventName, string propsJson) => new(
        1, DateTime.UtcNow, "ga4", 0, EventId, eventName, "client", DateTime.UtcNow,
        "u-42", Guid.NewGuid(), SessionKey, propsJson, "{}", Identity());

    private static EpConfig Config() => new()
    {
        DbConnString = "unused",
        Ga4ApiSecret = "s", Ga4MeasurementId = "G-T",
    };

    // ============================================================ transform

    [Fact]
    public void Single_product_event_builds_one_item_with_mapped_fields()
    {
        var props = JsonDocument.Parse(
            """
            {
              "product_id": "iphone-15",
              "product_name": "iPhone 15",
              "price": 250000,
              "currency": "IQD",
              "brand_name": "apple",
              "category_name": "smartphones",
              "variant_name": "128GB Black"
            }
            """).RootElement;

        var itemsJson = Ga4EcommerceTransform.BuildItemsJson("product_viewed", props);
        using var doc = JsonDocument.Parse(itemsJson);
        var item = doc.RootElement[0];

        Assert.Equal("iphone-15",   item.GetProperty("item_id").GetString());
        Assert.Equal("iPhone 15",   item.GetProperty("item_name").GetString());
        Assert.Equal(250000,        item.GetProperty("price").GetInt64());
        Assert.Equal("apple",       item.GetProperty("item_brand").GetString());
        Assert.Equal("smartphones", item.GetProperty("item_category").GetString());
        Assert.Equal("128GB Black", item.GetProperty("item_variant").GetString());
        Assert.Equal(1,             item.GetProperty("quantity").GetInt32()); // default 1
    }

    [Fact]
    public void Single_product_event_uses_declared_quantity_when_present()
    {
        var props = JsonDocument.Parse(
            """{"product_id":"iphone-15","quantity":3}""").RootElement;
        var itemsJson = Ga4EcommerceTransform.BuildItemsJson("product_added", props);
        using var doc = JsonDocument.Parse(itemsJson);
        Assert.Equal(3, doc.RootElement[0].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public void Multi_product_event_maps_each_element_of_products_array()
    {
        var props = JsonDocument.Parse(
            """
            {
              "value": 500000,
              "currency": "IQD",
              "products": [
                { "product_id": "iphone-15",   "product_name": "iPhone 15",   "price": 250000, "brand_name": "apple" },
                { "product_id": "galaxy-s24",  "product_name": "Galaxy S24",  "price": 250000, "brand_name": "samsung" }
              ]
            }
            """).RootElement;

        var itemsJson = Ga4EcommerceTransform.BuildItemsJson("checkout_started", props);
        using var doc = JsonDocument.Parse(itemsJson);
        var items = doc.RootElement;

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("iphone-15", items[0].GetProperty("item_id").GetString());
        Assert.Equal("apple",     items[0].GetProperty("item_brand").GetString());
        Assert.Equal("galaxy-s24", items[1].GetProperty("item_id").GetString());
        Assert.Equal("samsung",    items[1].GetProperty("item_brand").GetString());
    }

    [Fact]
    public void Missing_product_identity_yields_empty_items_array()
    {
        var props = JsonDocument.Parse("""{"quantity":1,"currency":"IQD"}""").RootElement;
        var itemsJson = Ga4EcommerceTransform.BuildItemsJson("product_viewed", props);
        using var doc = JsonDocument.Parse(itemsJson);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void Non_ecommerce_event_is_not_matched()
    {
        Assert.False(Ga4EcommerceTransform.NeedsItems("user_signed_up"));
        Assert.False(Ga4EcommerceTransform.NeedsItems("coupon_applied"));
        Assert.True(Ga4EcommerceTransform.NeedsItems("product_viewed"));
        Assert.True(Ga4EcommerceTransform.NeedsItems("cart_viewed"));
        Assert.True(Ga4EcommerceTransform.NeedsItems("order_completed"));
    }

    // ================================================== end-to-end via sender

    private static TrackingPlan Plan() => TrackingPlan.Parse(
        """
        {
          "events": {
            "product_viewed": {
              "origin": "client",
              "destinations": ["ga4"],
              "properties": ["product_id","product_name","price","currency","brand_name"]
            },
            "checkout_started": {
              "origin": "client",
              "destinations": ["ga4"],
              "properties": ["value","currency","products"]
            }
          },
          "destinations": {
            "ga4": {
              "events": {
                "product_viewed":  { "name": "view_item",       "properties": { "currency": "currency", "price": "value" } },
                "checkout_started":{ "name": "begin_checkout",  "properties": { "value": "value", "currency": "currency" } }
              }
            }
          }
        }
        """);

    [Fact]
    public async Task Ga4_sender_emits_view_item_with_items_array()
    {
        var stub = new StubHandler();
        await new Ga4Sender(Config(), Plan(), handler: stub).SendAsync(
            Item("product_viewed",
                """{"product_id":"iphone-15","product_name":"iPhone 15","price":250000,"currency":"IQD","brand_name":"apple"}"""),
            default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var evt = doc.RootElement.GetProperty("events")[0];
        Assert.Equal("view_item", evt.GetProperty("name").GetString());
        var p = evt.GetProperty("params");
        Assert.Equal("IQD", p.GetProperty("currency").GetString());
        Assert.Equal(250000, p.GetProperty("value").GetInt64());
        var items = p.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("iphone-15", items[0].GetProperty("item_id").GetString());
        Assert.Equal("iPhone 15", items[0].GetProperty("item_name").GetString());
        Assert.Equal("apple", items[0].GetProperty("item_brand").GetString());
    }

    [Fact]
    public async Task Ga4_sender_emits_begin_checkout_with_products_array_transformed()
    {
        var stub = new StubHandler();
        await new Ga4Sender(Config(), Plan(), handler: stub).SendAsync(
            Item("checkout_started",
                """
                {"value":500000,"currency":"IQD","products":[
                  {"product_id":"iphone-15","product_name":"iPhone 15","price":250000,"brand_name":"apple"},
                  {"product_id":"galaxy-s24","product_name":"Galaxy S24","price":250000,"brand_name":"samsung"}
                ]}
                """),
            default);

        using var doc = JsonDocument.Parse(stub.Requests[0].Body);
        var p = doc.RootElement.GetProperty("events")[0].GetProperty("params");
        Assert.Equal(500000, p.GetProperty("value").GetInt64());
        var items = p.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("iphone-15", items[0].GetProperty("item_id").GetString());
        Assert.Equal("galaxy-s24", items[1].GetProperty("item_id").GetString());
    }
}
