namespace EventPump.Config;

/// <summary>
/// Runtime index of all tenants (SPEC v1.2 §13.2 / §13.4). Built once at
/// process boot; immutable thereafter. Token → tenant is the auth hot path,
/// so it is a plain Dictionary lookup.
/// </summary>
public sealed class TenantRegistry
{
    private readonly Dictionary<string, TenantConfig> _byToken;
    private readonly Dictionary<string, TenantConfig> _byInternalToken;
    private readonly Dictionary<string, TenantConfig> _byAppId;

    public IReadOnlyCollection<TenantConfig> All { get; }

    private TenantRegistry(IReadOnlyList<TenantConfig> tenants)
    {
        _byToken = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        _byInternalToken = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        _byAppId = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        foreach (var t in tenants)
        {
            if (!_byAppId.TryAdd(t.AppId, t))
                throw new InvalidOperationException($"duplicate tenant app_id '{t.AppId}'");
            foreach (var token in t.ClientTokens)
            {
                if (!_byToken.TryAdd(token, t))
                    throw new InvalidOperationException(
                        $"tenant '{t.AppId}' shares a client_token with '{_byToken[token].AppId}'");
            }
            if (t.InternalToken.Length > 0)
            {
                if (!_byInternalToken.TryAdd(t.InternalToken, t))
                    throw new InvalidOperationException(
                        $"tenant '{t.AppId}' shares an internal_token with '{_byInternalToken[t.InternalToken].AppId}'");
            }
        }
        All = tenants;
    }

    /// <summary>
    /// SPEC §9.1: match a bearer token against every tenant's client_tokens.
    /// Returns null when no tenant claims it (401 unauthorized).
    /// </summary>
    public TenantConfig? ByClientToken(string token) => _byToken.GetValueOrDefault(token);

    /// <summary>
    /// SPEC §9.3: match a bearer token against every tenant's internal_token.
    /// Used by /internal/v1/events and DSR endpoints to resolve which tenant
    /// the caller is operating on.
    /// </summary>
    public TenantConfig? ByInternalToken(string token) => _byInternalToken.GetValueOrDefault(token);

    public TenantConfig? ByAppId(string appId) => _byAppId.GetValueOrDefault(appId);

    /// <summary>
    /// Resolution order (SPEC §13.4):
    ///   1. EP_TENANTS_DIR set → every *.json in that dir is a tenant.
    ///   2. EP_TENANTS_DIR unset → synthesise one tenant from the legacy
    ///      EP_* env vars + EP_TRACKING_PLAN. This is the back-compat path
    ///      for existing single-tenant deployments.
    /// A directory that exists but contains zero tenant files aborts boot:
    /// running the api with no tenants at all is a misconfiguration.
    /// </summary>
    public static TenantRegistry Load(EpConfig config)
    {
        var dir = Environment.GetEnvironmentVariable("EP_TENANTS_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            if (!Directory.Exists(dir))
                throw new InvalidOperationException($"EP_TENANTS_DIR '{dir}' does not exist");
            var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException(
                    $"EP_TENANTS_DIR '{dir}' contains no tenant files (expected *.json)");
            var tenants = files.Select(TenantConfig.Load).ToArray();
            return new TenantRegistry(tenants);
        }

        // Back-compat: build one tenant from legacy env.
        if (string.IsNullOrWhiteSpace(config.TrackingPlanPath))
            throw new InvalidOperationException(
                "EP_TENANTS_DIR is unset and EP_TRACKING_PLAN is not set — nothing to load");
        var plan = TrackingPlan.Load(config.TrackingPlanPath);
        return new TenantRegistry([TenantConfig.FromLegacyEnvironment(config, plan)]);
    }

    /// <summary>Direct construction for tests.</summary>
    public static TenantRegistry ForTesting(params TenantConfig[] tenants)
        => new(tenants);
}
