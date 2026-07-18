using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

public class TrackingPlanTests
{
    [Fact]
    public void Auto_injects_ep_attributes_synced_reserved_event()
    {
        var plan = TrackingPlan.Parse("""{ "events": {} }""");

        Assert.True(plan.Events.TryGetValue("ep_attributes_synced", out var reserved));
        Assert.True(reserved!.Reserved);
        Assert.Equal("server", reserved.Origin);
        Assert.Equal(["moengage_customer"], reserved.Destinations);
    }

    [Fact]
    public void Parses_attribute_allowlist_with_types_and_bounds()
    {
        var plan = TrackingPlan.Parse(
            """
            {
              "attributes": {
                "email":  { "type": "email", "max_length": 254 },
                "phone":  { "type": "e164",  "max_length": 16 },
                "gender": { "type": "enum",  "values": ["male", "female"] }
              },
              "events": {}
            }
            """);

        Assert.Equal("email", plan.Attributes["email"].Type);
        Assert.Equal(254, plan.Attributes["email"].MaxLength);
        Assert.Equal("e164", plan.Attributes["phone"].Type);
        Assert.Equal(["male", "female"], plan.Attributes["gender"].Values!);
    }

    [Theory]
    [InlineData("""{ "attributes": { "email": { "type": "unknown_type" } }, "events": {} }""", "unknown type")]
    [InlineData("""{ "attributes": { "gender": { "type": "enum" } }, "events": {} }""", "requires non-empty values")]
    [InlineData("""{ "attributes": { "BadName": { "type": "string" } }, "events": {} }""", "invalid attribute name")]
    public void Rejects_malformed_attribute_definitions(string json, string expectedFragment)
    {
        var error = Assert.Throws<InvalidDataException>(() => TrackingPlan.Parse(json));
        Assert.Contains(expectedFragment, error.Message);
    }

    // -------------------------------------------------- SPEC §6.2 renames

    [Fact]
    public void Resolve_event_name_returns_destination_override_or_canonical()
    {
        var plan = TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["ga4", "amplitude"] }
              },
              "destinations": {
                "ga4": { "events": { "order_placed": { "name": "purchase" } } }
              }
            }
            """);

        Assert.Equal("purchase", plan.ResolveEventName("order_placed", "ga4"));
        // R1: no rename block for amplitude -> canonical
        Assert.Equal("order_placed", plan.ResolveEventName("order_placed", "amplitude"));
        // R1: destination not in the map at all -> canonical
        Assert.Equal("order_placed", plan.ResolveEventName("order_placed", "meta"));
    }

    [Fact]
    public void Resolve_properties_renames_declared_keys_and_passes_others_through()
    {
        var plan = TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["ga4"] }
              },
              "destinations": {
                "ga4": {
                  "events": {
                    "order_placed": {
                      "name": "purchase",
                      "properties": { "order_id": "transaction_id", "revenue": "value" }
                    }
                  }
                }
              }
            }
            """);

        var renamed = plan.ResolvePropertiesJson(
            "order_placed", "ga4",
            """{"order_id":"o-1","revenue":9.99,"currency":"IQD","sku":"A1"}""");

        using var doc = System.Text.Json.JsonDocument.Parse(renamed);
        var root = doc.RootElement;
        Assert.Equal("o-1", root.GetProperty("transaction_id").GetString());
        Assert.Equal(9.99, root.GetProperty("value").GetDouble());
        // R3: unlisted properties pass through as-is
        Assert.Equal("IQD", root.GetProperty("currency").GetString());
        Assert.Equal("A1", root.GetProperty("sku").GetString());
        // renamed keys must NOT still be present under their old names
        Assert.False(root.TryGetProperty("order_id", out _));
        Assert.False(root.TryGetProperty("revenue", out _));
    }

    [Fact]
    public void Resolve_properties_returns_input_verbatim_when_no_rename_applies()
    {
        var plan = TrackingPlan.Parse("""{"events":{}}""");
        var input = """{"sku":"A1","currency":"IQD"}""";
        Assert.Equal(input, plan.ResolvePropertiesJson("anything", "ga4", input));
    }

    // -------------------------------------------- SPEC §6.2 R6: no name for adjust

    [Fact]
    public void Rejects_name_rename_under_adjust_at_boot()
    {
        var error = Assert.Throws<InvalidDataException>(() => TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["adjust"], "adjust_token": "T" }
              },
              "destinations": {
                "adjust": { "events": { "order_placed": { "name": "purchase" } } }
              }
            }
            """));

        Assert.Contains("destinations.adjust.events.order_placed.name is not allowed", error.Message);
        Assert.Contains("adjust_token", error.Message);
    }

    [Fact]
    public void Allows_property_rename_under_adjust()
    {
        // R6 rejects only `name` — properties (partner_params) are fine.
        var plan = TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["adjust"], "adjust_token": "T" }
              },
              "destinations": {
                "adjust": {
                  "events": {
                    "order_placed": { "properties": { "order_id": "transaction_id" } }
                  }
                }
              }
            }
            """);

        var renamed = plan.ResolvePropertiesJson(
            "order_placed", "adjust", """{"order_id":"o-1"}""");
        using var doc = System.Text.Json.JsonDocument.Parse(renamed);
        Assert.Equal("o-1", doc.RootElement.GetProperty("transaction_id").GetString());
    }

    // -------------------------------------------- SPEC §6.2 R7: validation

    [Fact]
    public void Rejects_rename_for_undefined_event_at_boot()
    {
        var error = Assert.Throws<InvalidDataException>(() => TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["ga4"] }
              },
              "destinations": {
                "ga4": { "events": { "orderr_placed": { "name": "purchase" } } }
              }
            }
            """));

        Assert.Contains("destinations.ga4.events references undefined event 'orderr_placed'", error.Message);
    }

    [Fact]
    public void Rejects_rename_when_destination_not_in_event_routing()
    {
        var error = Assert.Throws<InvalidDataException>(() => TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["ga4", "amplitude"] }
              },
              "destinations": {
                "meta": { "events": { "order_placed": { "name": "Purchase" } } }
              }
            }
            """));

        Assert.Contains(
            "destinations.meta.events.order_placed exists but 'order_placed' does not route to 'meta'",
            error.Message);
    }

    [Fact]
    public void Callers_cannot_override_the_auto_injected_reserved_event()
    {
        // Even if the plan declares it, TrackingPlan.Parse rewrites it to the reserved shape.
        var plan = TrackingPlan.Parse(
            """
            {
              "events": {
                "ep_attributes_synced": { "origin": "client", "destinations": ["ga4"] }
              }
            }
            """);

        var reserved = plan.Events["ep_attributes_synced"];
        Assert.True(reserved.Reserved);
        Assert.Equal("server", reserved.Origin);
        Assert.Equal(["moengage_customer"], reserved.Destinations);
    }
}
