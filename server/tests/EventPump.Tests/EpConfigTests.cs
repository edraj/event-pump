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
}

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection;
