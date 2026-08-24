using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Boot-time validation on TenantRegistry construction (SPEC v1.2 §13.4):
/// misconfigured tenants must fail loud at startup, not silently at first
/// send. All checks are exercised via `ForTesting` since the file-loading
/// path (`Load`) funnels through the same constructor.
/// </summary>
public class TenantRegistryTests
{
    private static TenantConfig Base(string appId = "acme") => new()
    {
        AppId = appId,
        TenantApiKey = $"{appId}-client-key",
        InternalToken = $"{appId}-internal-secret",
        Plan = TrackingPlan.Parse("""{"events":{}}"""),
    };

    // ------------------------------------------------ existing invariants

    [Fact]
    public void Duplicate_tenant_api_key_is_rejected()
    {
        var a = Base("acme");
        var b = Base("widgets") with { TenantApiKey = a.TenantApiKey };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(a, b));
        Assert.Contains("shares a tenant_api_key", error.Message);
    }

    [Fact]
    public void Same_tenant_api_key_and_internal_token_within_one_tenant_is_rejected()
    {
        var t = Base() with { InternalToken = "acme-client-key" };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("must be different values", error.Message);
    }

    // ------------------------------------------ destination-credential checks

    [Fact]
    public void Ga4_enabled_without_api_secret_fails_loud()
    {
        var t = Base() with { Ga4Enabled = true, Ga4MeasurementId = "G-X", Ga4ApiSecret = "" };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("ga4 enabled but api_secret is empty", error.Message);
    }

    [Fact]
    public void Ga4_enabled_without_either_measurement_id_or_firebase_app_id_fails_loud()
    {
        var t = Base() with { Ga4Enabled = true, Ga4ApiSecret = "secret" };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("ga4 enabled but neither measurement_id nor firebase_app_id", error.Message);
    }

    [Fact]
    public void Ga4_enabled_with_only_firebase_app_id_is_accepted()
    {
        // Ga4Sender.cs supports either id — accept firebase-only as legitimate.
        var t = Base() with
        {
            Ga4Enabled = true,
            Ga4ApiSecret = "secret",
            Ga4FirebaseAppId = "1:123:web:abc",
        };

        var registry = TenantRegistry.ForTesting(t);
        Assert.NotNull(registry.ByAppId("acme"));
    }

    [Fact]
    public void Amplitude_enabled_without_api_key_fails_loud()
    {
        var t = Base() with { AmplitudeEnabled = true, AmplitudeApiKey = "" };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("amplitude enabled but api_key is empty", error.Message);
    }

    [Fact]
    public void MoEngage_enabled_without_credentials_fails_loud()
    {
        var missingAppId = Base() with { MoEngageEnabled = true, MoEngageApiKey = "k" };
        var errorAppId = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(missingAppId));
        Assert.Contains("moengage_app_id", errorAppId.Message);

        var missingKey = Base("widgets") with { MoEngageEnabled = true, MoEngageAppId = "MOE-APP" };
        var errorKey = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(missingKey));
        Assert.Contains("api_key", errorKey.Message);
    }

    [Fact]
    public void Adjust_enabled_without_app_token_fails_loud()
    {
        var t = Base() with { AdjustEnabled = true, AdjustAppToken = "" };

        var error = Assert.Throws<InvalidOperationException>(() => TenantRegistry.ForTesting(t));
        Assert.Contains("adjust enabled but app_token is empty", error.Message);
    }

    [Fact]
    public void Meta_enabled_without_credentials_fails_loud()
    {
        var missingPixel = Base() with { MetaEnabled = true, MetaAccessToken = "t" };
        var errorPixel = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(missingPixel));
        Assert.Contains("pixel_id", errorPixel.Message);

        var missingToken = Base("widgets") with { MetaEnabled = true, MetaPixelId = "PIX" };
        var errorToken = Assert.Throws<InvalidOperationException>(
            () => TenantRegistry.ForTesting(missingToken));
        Assert.Contains("access_token", errorToken.Message);
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
        Assert.NotNull(registry.ByAppId("acme"));
    }
}
