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
