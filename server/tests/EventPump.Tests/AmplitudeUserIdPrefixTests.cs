using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// `destination_config.amplitude.user_id_prefix` in a tenant file (SPEC
/// §13.2). Off unless the tenant names one: switching it on gives every
/// existing user a new Amplitude profile, so a deploy must never do it.
/// </summary>
public class AmplitudeUserIdPrefixTests
{
    private static string TenantJson(string amplitude) =>
        $$"""
        {
          "app_id": "zainmart",
          "tenant_api_key": "client-key",
          "internal_token": "internal-secret",
          "destination_config": { "amplitude": {{amplitude}} }
        }
        """;

    [Fact]
    public void Is_read_from_the_tenant_file()
        => Assert.Equal("usr_", TenantConfig.Parse(TenantJson(
            """{ "enabled": true, "api_key": "k", "user_id_prefix": "usr_" }""")).AmplitudeUserIdPrefix);

    [Theory]
    [InlineData("""{ "enabled": true, "api_key": "k" }""")]
    [InlineData("""{ "enabled": true, "api_key": "k", "user_id_prefix": null }""")]
    [InlineData("""{ "enabled": true, "api_key": "k", "user_id_prefix": "" }""")]
    [InlineData("""{ "enabled": true, "api_key": "k", "user_id_prefix": "   " }""")]
    public void Is_off_unless_the_tenant_names_one(string amplitude)
        => Assert.Equal("", TenantConfig.Parse(TenantJson(amplitude)).AmplitudeUserIdPrefix);
}
