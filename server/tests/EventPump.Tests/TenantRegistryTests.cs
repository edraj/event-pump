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
}
