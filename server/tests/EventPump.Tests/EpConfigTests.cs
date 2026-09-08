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

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    public void A_default_on_flag_reads_every_spelling_of_off(string value, bool expected)
    {
        // Read as `!= "false"` this left erasure ON for `False` and `0` — an
        // operator opting a destination out and being ignored, silently.
        using var env = MinimalEnv().Set("EP_MOENGAGE_ERASURE_ENABLED", value);

        Assert.Equal(expected, EpConfig.FromEnvironment().MoEngageErasureEnabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("True")]
    [InlineData("yes")]
    public void A_default_off_flag_reads_every_spelling_of_on(string value)
    {
        using var env = MinimalEnv().Set("EP_GA4_ENABLED", value);

        Assert.True(EpConfig.FromEnvironment().Ga4Enabled);
    }

    [Fact]
    public void A_boolean_that_is_neither_stops_the_boot_rather_than_defaulting()
    {
        // Silently resolving a typo to the default is how a destination ends
        // up erasing when it was told not to.
        using var env = MinimalEnv().Set("EP_ADJUST_ERASURE_ENABLED", "flase");

        var ex = Assert.Throws<InvalidOperationException>(EpConfig.FromEnvironment);

        Assert.Contains("EP_ADJUST_ERASURE_ENABLED", ex.Message);
        Assert.Contains("flase", ex.Message);
    }

    /// <summary>
    /// Console.SetError is process-global and only the `env` collection is
    /// serialized against this one, so the `pg` tests run alongside and their
    /// stderr lands in this writer too. Every assertion on the result has to
    /// be about the line we are looking for, never about the buffer being
    /// empty.
    /// </summary>
    private static string CapturedStderr(Action act)
    {
        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            act();
        }
        finally
        {
            Console.SetError(previous);
        }
        return captured.ToString();
    }

    [Theory]
    // Was `== "true"`, case-sensitively, so `True` read as off.
    [InlineData("EP_GA4_ENABLED", "True", "reads as true")]
    // Was `!= "false"`, so `0` read as on.
    [InlineData("EP_MOENGAGE_ERASURE_ENABLED", "0", "reads as false")]
    public void A_value_this_build_reads_differently_says_so_on_the_way_up(
        string name, string value, string expected)
    {
        // An operator who changed nothing gets a destination that was dark
        // sending live traffic, or an erasure that stops running. Only the
        // boot that flips it can say so.
        using var env = MinimalEnv().Set(name, value);
        var stderr = CapturedStderr(() => EpConfig.FromEnvironment());

        Assert.Contains(name, stderr);
        Assert.Contains(expected, stderr);
    }

    [Theory]
    [InlineData("EP_GA4_ENABLED", "true")]
    [InlineData("EP_MOENGAGE_ERASURE_ENABLED", "false")]
    public void A_value_that_always_meant_the_same_thing_stays_quiet(string name, string value)
    {
        using var env = MinimalEnv().Set(name, value);
        var stderr = CapturedStderr(() => EpConfig.FromEnvironment());

        Assert.DoesNotContain(name, stderr);
    }

    [Fact]
    public void An_unset_boolean_keeps_its_default()
    {
        using var env = MinimalEnv()
            .Set("EP_MOENGAGE_ERASURE_ENABLED", null)
            .Set("EP_GA4_ENABLED", null);

        var config = EpConfig.FromEnvironment();

        Assert.True(config.MoEngageErasureEnabled);
        Assert.False(config.Ga4Enabled);
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection;
