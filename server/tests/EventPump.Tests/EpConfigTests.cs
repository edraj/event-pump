using EventPump.Config;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Env parsing (SPEC §13). These mutate process environment variables, so they
/// live in their own non-parallel collection — nothing else reads EP_* at
/// runtime, but two of these running at once would see each other's writes.
/// </summary>
[Collection("env")]
public class EpConfigTests
{
    /// <summary>Sets vars for the duration of a test and restores them after.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        public EnvScope Set(string name, string? value)
        {
            _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static EnvScope MinimalEnv() => new EnvScope()
        .Set("EP_DB_CONNSTRING", "Host=127.0.0.1;Username=u;Database=d")
        .Set("EP_TRACKING_PLAN", "/nonexistent/plan.json")
        .Set("EP_TENANTS_DIR", null)
        .Set("EP_CLIENT_TOKENS", null);

    [Fact]
    public void Retired_client_tokens_var_stops_the_boot()
    {
        // Pre-v1.2 this mapped several app_ids onto one process. Nothing reads
        // it now, so a deployment that still sets it would boot and silently
        // file every tenant's traffic under EP_LEGACY_APP_ID — re-bucketing
        // error_reports, whose aggregation keys on (day, app_id, stack_hash),
        // mid-history. Refusing to start is the only safe reading.
        using var env = MinimalEnv().Set("EP_CLIENT_TOKENS", "zainmart:tok-a,other:tok-b");

        var ex = Assert.Throws<InvalidOperationException>(EpConfig.FromEnvironment);
        Assert.Contains("EP_CLIENT_TOKENS", ex.Message);
        Assert.Contains("EP_TENANTS_DIR", ex.Message);
    }

    [Fact]
    public void Config_loads_once_the_retired_var_is_gone()
    {
        using var env = MinimalEnv();

        var config = EpConfig.FromEnvironment();

        Assert.Equal("zainmart", config.LegacyAppId);
        Assert.Equal("/nonexistent/plan.json", config.TrackingPlanPath);
    }

    [Fact]
    public void Legacy_env_path_boots_without_an_internal_token()
    {
        // Pre-v1.2 a client-only install could leave EP_INTERNAL_TOKEN unset and
        // ApiApp simply 401'd the internal routes. Demanding one when the tenant
        // is synthesised from env would crashloop the api and worker on upgrade,
        // so the back-compat path still accepts an empty secret — and an empty
        // secret authenticates nothing, because ResolveInternalTenant() skips
        // zero-length tokens. A tenant *file* must still declare both (see
        // TenantRegistryTests.A_tenant_file_must_declare_an_internal_token).
        var planPath = Path.Combine(Path.GetTempPath(), $"ep-plan-{Guid.NewGuid():N}.json");
        File.WriteAllText(planPath,
            """
            { "events": { "product_viewed": { "origin": "client", "destinations": [] } } }
            """);
        try
        {
            using var env = MinimalEnv()
                .Set("EP_TRACKING_PLAN", planPath)
                .Set("EP_TENANT_API_KEY", "client-key")
                .Set("EP_INTERNAL_TOKEN", null);

            var registry = TenantRegistry.Load(EpConfig.FromEnvironment());

            var tenant = Assert.Single(registry.All);
            Assert.Equal("zainmart", tenant.AppId);
            Assert.Equal("", tenant.InternalToken);
        }
        finally
        {
            File.Delete(planPath);
        }
    }

    [Fact]
    public void Retired_var_message_names_both_replacement_secrets()
    {
        // An operator upgrading from EP_CLIENT_TOKENS hits this message first;
        // it has to name every var they now need, including the server-side
        // secret and the constraint that it differ from the client key.
        using var env = MinimalEnv().Set("EP_CLIENT_TOKENS", "zainmart:tok-a");

        var ex = Assert.Throws<InvalidOperationException>(EpConfig.FromEnvironment);

        Assert.Contains("EP_TENANT_API_KEY", ex.Message);
        Assert.Contains("EP_INTERNAL_TOKEN", ex.Message);
        Assert.Contains("must not repeat", ex.Message);
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection;
