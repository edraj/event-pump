using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

public class ErasureConfigTests
{
    private static string TenantJson(string destinationConfig) =>
        $$"""
        {
          "app_id": "zainmart",
          "tenant_api_key": "client-key",
          "internal_token": "internal-secret",
          "destination_config": {{destinationConfig}}
        }
        """;

    [Theory]
    [InlineData("ep_erasure_requested")]
    [InlineData("ep_attributes_erasure_requested")]
    public void Auto_injects_the_reserved_erasure_events(string eventName)
    {
        var plan = TrackingPlan.Parse("""{ "events": {} }""");

        Assert.True(plan.Events.TryGetValue(eventName, out var reserved));
        Assert.True(reserved!.Reserved);
        Assert.Equal("server", reserved.Origin);
    }

    [Theory]
    [InlineData("ep_erasure_requested")]
    [InlineData("ep_attributes_erasure_requested")]
    public void Reserved_erasure_events_carry_no_destinations_of_their_own(string eventName)
    {
        var plan = TrackingPlan.Parse("""{ "events": {} }""");

        Assert.Empty(plan.Events[eventName].Destinations);
    }

    [Theory]
    [InlineData("ep_erasure_requested")]
    [InlineData("ep_attributes_erasure_requested")]
    public void A_producer_cannot_redefine_a_reserved_erasure_event(string eventName)
    {
        var plan = TrackingPlan.Parse(
            $$"""
            {
              "events": {
                "{{eventName}}": { "origin": "client", "destinations": ["ga4"] }
              }
            }
            """);

        var reserved = plan.Events[eventName];
        Assert.True(reserved.Reserved);
        Assert.Equal("server", reserved.Origin);
        Assert.Empty(reserved.Destinations);
    }

    [Fact]
    public void Erasure_defaults_on_when_the_tenant_file_is_silent()
    {
        var tenant = TenantConfig.Parse(TenantJson("""{ "moengage": { "enabled": true } }"""));

        Assert.True(tenant.MoEngageErasureEnabled);
        Assert.True(tenant.AdjustErasureEnabled);
        Assert.True(tenant.AmplitudeErasureEnabled);
        Assert.True(tenant.Ga4ErasureEnabled);
    }

    [Fact]
    public void Erasure_can_be_turned_off_for_one_vendor_without_touching_the_rest()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """
            {
              "moengage": { "enabled": true, "erasure_enabled": false },
              "adjust":   { "enabled": true }
            }
            """));

        Assert.False(tenant.MoEngageErasureEnabled);
        Assert.True(tenant.AdjustErasureEnabled);
    }

    [Fact]
    public void Erasure_is_independent_of_the_attributes_gate()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """
            { "moengage": { "enabled": true,
                            "attributes_enabled": false,
                            "erasure_enabled": true } }
            """));

        Assert.False(tenant.MoEngageAttributesEnabled);
        Assert.True(tenant.MoEngageErasureEnabled);
    }

    [Fact]
    public void Fans_out_to_every_enabled_vendor()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """
            {
              "moengage":  { "enabled": true },
              "adjust":    { "enabled": true },
              "amplitude": { "enabled": true },
              "ga4":       { "enabled": true }
            }
            """));

        Assert.Equal(
            ["moengage_erasure", "adjust_erasure", "amplitude_erasure", "ga4_erasure"],
            tenant.ErasureDestinations());
    }

    [Fact]
    public void A_vendor_that_is_off_contributes_no_erasure_destination()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """
            { "moengage": { "enabled": true }, "adjust": { "enabled": false } }
            """));

        Assert.Equal(["moengage_erasure"], tenant.ErasureDestinations());
    }

    [Fact]
    public void An_enabled_vendor_with_erasure_off_contributes_nothing_either()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """
            {
              "moengage": { "enabled": true },
              "adjust":   { "enabled": true, "erasure_enabled": false }
            }
            """));

        Assert.Equal(["moengage_erasure"], tenant.ErasureDestinations());
    }

    [Fact]
    public void No_vendors_enabled_means_a_local_only_erasure()
    {
        var tenant = TenantConfig.Parse(TenantJson("{}"));

        Assert.Empty(tenant.ErasureDestinations());
    }

    [Fact]
    public void Meta_never_appears_it_exposes_no_deletion_api()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """{ "meta": { "enabled": true, "pixel_id": "1", "access_token": "t" } }"""));

        Assert.Empty(tenant.ErasureDestinations());
    }

    [Fact]
    public void Tenant_info_lists_erasure_destinations_under_their_own_key()
    {
        var tenant = TenantConfig.Parse(TenantJson(
            """{ "moengage": { "enabled": true }, "adjust": { "enabled": true } }"""));

        Assert.Equal(["moengage_erasure", "adjust_erasure"], tenant.ErasureDestinations());
        Assert.Equal(["moengage_erasure"], tenant.AttributesErasureDestinations());
    }

    [Fact]
    public void Legacy_environment_carries_the_erasure_gates_onto_the_tenant()
    {
        var config = new EpConfig
        {
            DbConnString = "unused-in-tests",
            MoEngageErasureEnabled = false,
            AdjustErasureEnabled = false,
        };

        var tenant = TenantConfig.FromLegacyEnvironment(config, TrackingPlan.Parse("{}"));

        Assert.False(tenant.MoEngageErasureEnabled);
        Assert.False(tenant.AdjustErasureEnabled);
        Assert.True(tenant.AmplitudeErasureEnabled);
    }

    [Fact]
    public void Erasure_defaults_on_for_the_legacy_environment_too()
    {
        var config = new EpConfig { DbConnString = "unused-in-tests" };

        var tenant = TenantConfig.FromLegacyEnvironment(config, TrackingPlan.Parse("{}"));

        Assert.True(tenant.MoEngageErasureEnabled);
        Assert.True(tenant.Ga4ErasureEnabled);
    }
}
