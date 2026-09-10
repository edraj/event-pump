using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// `adjust.environment` in a tenant file (SPEC §13.2). Adjust recognises
/// `sandbox` and `production` and treats everything else as production while
/// still answering 200, so a value it does not know is a UAT tenant firing its
/// test traffic at live attribution with nothing to report it. Parse is the
/// last place that is knowable, so it is checked there rather than at send.
/// The env-var half of the same rule lives in EpConfigTests.
/// </summary>
public class AdjustEnvironmentTests
{
    private static string TenantJson(string adjust) =>
        $$"""
        {
          "app_id": "zainmart",
          "tenant_api_key": "client-key",
          "internal_token": "internal-secret",
          "destination_config": { "adjust": {{adjust}} }
        }
        """;

    [Theory]
    [InlineData("sandbox", "sandbox")]
    [InlineData("production", "production")]
    // Adjust reads the value literally, so the canonical spelling is what goes
    // on the wire — but a capitalisation is not worth refusing a boot over.
    [InlineData("Sandbox", "sandbox")]
    [InlineData("PRODUCTION", "production")]
    [InlineData("  sandbox  ", "sandbox")]
    public void A_recognised_environment_is_normalised(string value, string expected)
    {
        var tenant = TenantConfig.Parse(
            TenantJson($$"""{ "enabled": true, "app_token": "t", "environment": "{{value}}" }"""));

        Assert.Equal(expected, tenant.AdjustEnvironment);
    }

    [Theory]
    [InlineData("sanbox")]
    [InlineData("uat")]
    [InlineData("test")]
    [InlineData("prod")]
    public void An_unrecognised_environment_stops_the_boot(string value)
    {
        var error = Assert.Throws<InvalidDataException>(() => TenantConfig.Parse(
            TenantJson($$"""{ "enabled": true, "app_token": "t", "environment": "{{value}}" }"""),
            "zainmart.jsonc"));

        // The message has to carry the file and what was written in it; an
        // operator staring at a boot failure has neither otherwise.
        Assert.Contains("zainmart.jsonc", error.Message);
        Assert.Contains("adjust.environment", error.Message);
        Assert.Contains(value, error.Message);
    }

    [Theory]
    // Absent, null and empty are all how a tenant file says "leave this
    // alone", and whitespace-only is the same statement typed carelessly —
    // sending it verbatim would land on Adjust's production default anyway,
    // with the spaces on the wire to prove nobody looked.
    [InlineData("""{ "enabled": true, "app_token": "t" }""")]
    [InlineData("""{ "enabled": true, "app_token": "t", "environment": null }""")]
    [InlineData("""{ "enabled": true, "app_token": "t", "environment": "" }""")]
    [InlineData("""{ "enabled": true, "app_token": "t", "environment": "   " }""")]
    public void An_unstated_environment_stays_unset(string adjust)
    {
        Assert.Null(TenantConfig.Parse(TenantJson(adjust)).AdjustEnvironment);
    }

    /// <summary>
    /// A tenant with adjust switched off still gets its file checked: the
    /// typo is in the file either way, and finding it on the boot that
    /// switches the destination on is finding it in production.
    /// </summary>
    [Fact]
    public void A_disabled_adjust_block_is_checked_too()
    {
        Assert.Throws<InvalidDataException>(() => TenantConfig.Parse(
            TenantJson("""{ "enabled": false, "environment": "sanbox" }""")));
    }

    /// <summary>
    /// The legacy single-tenant path reads the same field off EpConfig, where
    /// EP_ADJUST_ENVIRONMENT has already been through the same check.
    /// </summary>
    [Fact]
    public void The_legacy_environment_carries_the_setting_onto_the_tenant()
    {
        var config = new EpConfig { DbConnString = "unused-in-tests", AdjustEnvironment = "sandbox" };

        var tenant = TenantConfig.FromLegacyEnvironment(config, TrackingPlan.Parse("{}"));

        Assert.Equal("sandbox", tenant.AdjustEnvironment);
    }
}
