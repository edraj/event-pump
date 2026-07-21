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
    public void Resolve_properties_renames_listed_keys_and_drops_unlisted_ones()
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
        // SPEC §6.2 (revised): properties map is an ALLOWLIST — unlisted keys are dropped.
        Assert.False(root.TryGetProperty("currency", out _));
        Assert.False(root.TryGetProperty("sku", out _));
        // renamed keys must not still appear under their canonical names either
        Assert.False(root.TryGetProperty("order_id", out _));
        Assert.False(root.TryGetProperty("revenue", out _));
    }

    [Fact]
    public void Resolve_properties_pass_through_when_no_map_declared_for_destination()
    {
        // R1 fallback: no `destinations.<dest>` block AT ALL for the event -> full passthrough.
        var plan = TrackingPlan.Parse(
            """
            {
              "events": {
                "order_placed": { "origin": "server", "destinations": ["moengage"] }
              }
            }
            """);

        var result = plan.ResolvePropertiesJson(
            "order_placed", "moengage",
            """{"sku":"A1","currency":"IQD"}""");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("A1", doc.RootElement.GetProperty("sku").GetString());
        Assert.Equal("IQD", doc.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public void Resolve_properties_empty_map_drops_everything()
    {
        // Explicit "no properties for this destination" — declare an empty map.
        var plan = TrackingPlan.Parse(
            """
            {
              "events": { "user_logged_out": { "origin": "client", "destinations": ["ga4"] } },
              "destinations": {
                "ga4": { "events": { "user_logged_out": { "name": "logout", "properties": {} } } }
              }
            }
            """);

        var result = plan.ResolvePropertiesJson(
            "user_logged_out", "ga4", """{"reason":"idle"}""");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("reason", out _));
    }

    [Fact]
    public void Rejects_destination_property_not_declared_in_base_schema()
    {
        var error = Assert.Throws<InvalidDataException>(() => TrackingPlan.Parse(
            """
            {
              "events": {
                "user_signed_up": {
                  "origin": "client",
                  "destinations": ["ga4"],
                  "properties": ["user_id", "method", "type"]
                }
              },
              "destinations": {
                "ga4": {
                  "events": {
                    "user_signed_up": {
                      "name": "sign_up",
                      "properties": { "method": "method", "email": "email" }
                    }
                  }
                }
              }
            }
            """));

        Assert.Contains(
            "destinations.ga4.events.user_signed_up.properties references 'email' " +
            "which is not in the base properties of 'user_signed_up'",
            error.Message);
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
    public void Shipped_zainmart_example_plan_loads_cleanly()
    {
        // The zainmart example plan is a real 28-event plan with full base
        // schemas + per-destination allowlists. Loading it exercises R6/R7
        // and the property-subset check end-to-end.
        var path = System.IO.Path.Combine(
            new System.IO.DirectoryInfo(RepoPaths.ServerRoot).Parent!.FullName,
            "deploy", "tracking-plan.zainmart.example.json");
        var plan = TrackingPlan.Load(path);
        Assert.True(plan.Events.Count >= 28);
        Assert.Contains("order_completed", plan.Events.Keys);
        Assert.Equal("purchase", plan.ResolveEventName("order_completed", "ga4"));
        Assert.Equal("Order Placed", plan.ResolveEventName("order_completed", "moengage"));
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
