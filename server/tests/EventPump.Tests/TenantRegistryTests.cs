using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Boot-time validation of the tenant set (SPEC v1.2 §13.2). These rules are
/// the only thing between a hand-edited tenant file and a silent cross-tenant
/// authentication hole, so each one is pinned here rather than left to the
/// reviewer of the next tenant file.
/// </summary>
public class TenantRegistryTests
{
    private static TrackingPlan Plan() => TrackingPlan.Parse(
        """
        { "events": { "product_viewed": { "origin": "client", "destinations": [] } } }
        """);

    private static TenantConfig Tenant(string appId, string clientKey, string internalToken)
        => new()
        {
            AppId = appId,
            TenantApiKey = clientKey,
            InternalToken = internalToken,
            Plan = Plan(),
        };

    [Fact]
    public void One_tenants_client_key_may_not_be_anothers_internal_token()
    {
        // ResolveInternalTenant scans *every* tenant's internal_token, so this
        // collision would let acme's client key — which ships inside acme's APK
        // and web bundle — authenticate as widgets on POST /internal/v1/events
        // and the DSR erasure route. Two files edited from the same
        // REPLACE_ME_* template is all it takes.
        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(
            Tenant("acme", "shared-value", "acme-internal"),
            Tenant("widgets", "widgets-client", "shared-value")));

        Assert.Contains("acme", ex.Message);
        Assert.Contains("widgets", ex.Message);
        Assert.Contains("internal listener", ex.Message);
    }

    [Fact]
    public void The_collision_is_caught_whichever_tenant_loads_first()
    {
        // The two indexes fill in file order, so the check runs after the loop
        // rather than inside it — neither ordering may slip through.
        Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(
            Tenant("widgets", "widgets-client", "shared-value"),
            Tenant("acme", "shared-value", "acme-internal")));
    }

    [Fact]
    public void A_tenant_may_not_reuse_its_own_client_key_as_its_internal_token()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(
            Tenant("acme", "same", "same")));

        Assert.Contains("must be different values", ex.Message);
    }

    [Fact]
    public void A_tenant_file_must_declare_an_internal_token()
    {
        // Only the legacy env path may omit it — see
        // EpConfigTests.Legacy_env_path_boots_without_an_internal_token.
        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(
            Tenant("acme", "acme-client", "")));

        Assert.Contains("has no internal_token", ex.Message);
    }

    [Fact]
    public void Duplicate_secrets_within_one_tier_are_rejected_too()
    {
        Assert.Contains("shares a tenant_api_key", Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(
                Tenant("acme", "same-client", "acme-internal"),
                Tenant("widgets", "same-client", "widgets-internal"))).Message);

        Assert.Contains("shares an internal_token", Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(
                Tenant("acme", "acme-client", "same-internal"),
                Tenant("widgets", "widgets-client", "same-internal"))).Message);
    }

    [Fact]
    public void Distinct_secrets_across_tenants_are_accepted()
    {
        var registry = TenantRegistry.ForTesting(
            Tenant("acme", "acme-client", "acme-internal"),
            Tenant("widgets", "widgets-client", "widgets-internal"));

        Assert.Equal(2, registry.All.Count);
    }

    /// <summary>
    /// A destination enabled without its credentials is knowable at boot, and
    /// left to delivery time it costs a tenant hours of events piling up
    /// `failed` behind circuit-breaker backoff before anyone notices.
    /// </summary>
    [Theory]
    [InlineData("ga4", "api_secret")]
    [InlineData("amplitude", "api_key")]
    [InlineData("moengage", "moengage_app_id")]
    [InlineData("adjust", "app_token")]
    [InlineData("meta", "pixel_id")]
    public void An_enabled_destination_without_its_credentials_stops_the_boot(
        string destination, string field)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(WithDestination(destination, complete: false)));

        Assert.Contains("acme", ex.Message);
        Assert.Contains(destination, ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("ga4")]
    [InlineData("amplitude")]
    [InlineData("moengage")]
    [InlineData("adjust")]
    [InlineData("meta")]
    public void A_destination_with_its_credentials_boots(string destination)
    {
        var registry = TenantRegistry.ForTesting(WithDestination(destination, complete: true));

        Assert.Single(registry.All);
    }

    /// <summary>
    /// GA4 keys a stream by a web measurement_id or a Firebase app id, and the
    /// sender picks per identity handle. Either alone is a working config;
    /// neither is a 400 on every send.
    /// </summary>
    [Fact]
    public void Ga4_takes_a_firebase_app_id_in_place_of_a_measurement_id()
    {
        var registry = TenantRegistry.ForTesting(Tenant("acme", "acme-client", "acme-internal") with
        {
            Ga4Enabled = true,
            Ga4ApiSecret = "secret",
            Ga4FirebaseAppId = "1:1234:android:abcd",
        });

        Assert.Single(registry.All);
    }

    [Fact]
    public void Ga4_with_neither_stream_identifier_stops_the_boot()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(Tenant("acme", "acme-client", "acme-internal") with
            {
                Ga4Enabled = true,
                Ga4ApiSecret = "secret",
            }));

        Assert.Contains("neither measurement_id nor firebase_app_id", ex.Message);
    }

    /// <summary>
    /// Scaffolding a destination with empty credentials and switching it on
    /// later is the normal way to fill in a tenant file.
    /// </summary>
    [Fact]
    public void A_disabled_destination_is_not_checked()
    {
        var registry = TenantRegistry.ForTesting(Tenant("acme", "acme-client", "acme-internal"));

        Assert.Single(registry.All);
    }

    private static TenantConfig WithDestination(string destination, bool complete)
    {
        var tenant = Tenant("acme", "acme-client", "acme-internal");
        return destination switch
        {
            "ga4" => tenant with
            {
                Ga4Enabled = true,
                Ga4MeasurementId = "G-ABC",
                Ga4ApiSecret = complete ? "secret" : "",
            },
            "amplitude" => tenant with
            {
                AmplitudeEnabled = true,
                AmplitudeApiKey = complete ? "amp-key" : "",
            },
            "moengage" => tenant with
            {
                MoEngageEnabled = true,
                MoEngageApiKey = "moe-key",
                MoEngageAppId = complete ? "MOE-APP" : "",
            },
            "adjust" => tenant with
            {
                AdjustEnabled = true,
                AdjustAppToken = complete ? "adj-token" : "",
            },
            _ => tenant with
            {
                MetaEnabled = true,
                MetaAccessToken = "meta-token",
                MetaPixelId = complete ? "123" : "",
            },
        };
    }
}
