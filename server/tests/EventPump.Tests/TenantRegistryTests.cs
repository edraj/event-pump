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

    // ------------------------------------ destination-credential validation
    // A typo'd api key or a forgotten measurement_id used to boot cleanly and
    // only surface hours later as the outbox filled with delivery rows failing
    // at send time. Each check below pins one enabled destination's required
    // credential; the disabled-scaffolding test at the bottom pins the negative.

    private static TenantConfig Base(string appId = "acme")
        => Tenant(appId, $"{appId}-client", $"{appId}-internal");

    [Fact]
    public void Ga4_enabled_without_api_secret_fails_loud()
    {
        var t = Base() with { Ga4Enabled = true, Ga4MeasurementId = "G-X", Ga4ApiSecret = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("ga4 enabled but api_secret is empty", ex.Message);
    }

    [Fact]
    public void Ga4_enabled_without_either_measurement_id_or_firebase_app_id_fails_loud()
    {
        var t = Base() with { Ga4Enabled = true, Ga4ApiSecret = "secret" };

        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("ga4 enabled but neither measurement_id nor firebase_app_id", ex.Message);
    }

    [Fact]
    public void Ga4_enabled_with_only_firebase_app_id_is_accepted()
    {
        // Ga4Sender supports either identifier — accept firebase-only as legitimate.
        var t = Base() with
        {
            Ga4Enabled = true,
            Ga4ApiSecret = "secret",
            Ga4FirebaseAppId = "1:123:web:abc",
        };

        var registry = TenantRegistry.ForTesting(t);
        Assert.Single(registry.All);
    }

    [Fact]
    public void Amplitude_enabled_without_api_key_fails_loud()
    {
        var t = Base() with { AmplitudeEnabled = true, AmplitudeApiKey = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("amplitude enabled but api_key is empty", ex.Message);
    }

    [Fact]
    public void MoEngage_enabled_without_credentials_fails_loud()
    {
        var missingAppId = Base() with { MoEngageEnabled = true, MoEngageApiKey = "k" };
        Assert.Contains("moengage_app_id",
            Assert.Throws<InvalidOperationException>(
                () => TenantRegistry.ForTesting(missingAppId)).Message);

        var missingKey = Base("widgets") with { MoEngageEnabled = true, MoEngageAppId = "MOE-APP" };
        Assert.Contains("api_key",
            Assert.Throws<InvalidOperationException>(
                () => TenantRegistry.ForTesting(missingKey)).Message);
    }

    [Fact]
    public void Adjust_enabled_without_app_token_fails_loud()
    {
        var t = Base() with { AdjustEnabled = true, AdjustAppToken = "" };

        var ex = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("adjust enabled but app_token is empty", ex.Message);
    }

    [Fact]
    public void Meta_enabled_without_credentials_fails_loud()
    {
        var missingPixel = Base() with { MetaEnabled = true, MetaAccessToken = "t" };
        Assert.Contains("pixel_id",
            Assert.Throws<InvalidOperationException>(
                () => TenantRegistry.ForTesting(missingPixel)).Message);

        var missingToken = Base("widgets") with { MetaEnabled = true, MetaPixelId = "PIX" };
        Assert.Contains("access_token",
            Assert.Throws<InvalidOperationException>(
                () => TenantRegistry.ForTesting(missingToken)).Message);
    }

    [Fact]
    public void Destinations_left_disabled_are_not_validated()
    {
        // The whole point of `enabled: false` is that credentials aren't
        // required — a tenant might scaffold Meta before signing off on it.
        var t = Base() with
        {
            MetaEnabled = false,
            MetaPixelId = "",
            MetaAccessToken = "",
        };

        var registry = TenantRegistry.ForTesting(t);
        Assert.Single(registry.All);
    }
}
