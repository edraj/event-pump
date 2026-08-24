namespace EventPump.Config;

/// <summary>
/// Runtime index of all tenants (SPEC v1.2 §13.2 / §13.4). Built once at
/// process boot; immutable thereafter. The dictionaries below exist only to
/// reject a duplicate app_id / shared secret at boot — auth itself does NOT
/// use them: ApiApp scans <see cref="All"/> with a fixed-time compare and no
/// early exit, so a near-miss token cannot be told from a wrong one by
/// timing. A hash lookup would give that away.
/// </summary>
public sealed class TenantRegistry
{
    public IReadOnlyCollection<TenantConfig> All { get; }

    /// <param name="legacy">
    /// True only for the synthesised single tenant of the back-compat env path
    /// (SPEC §13.4). A pre-v1.2 install that ingested client traffic only never
    /// had to set EP_INTERNAL_TOKEN — ApiApp simply 401'd the internal routes —
    /// so demanding one here would crashloop the api and worker on upgrade.
    /// ResolveInternalTenant() skips empty tokens, so an unset secret keeps
    /// meaning "internal listener is closed", exactly as it did before. A
    /// tenant *file*, which is always written fresh for v1.2, must still
    /// declare both secrets.
    /// </param>
    private TenantRegistry(IReadOnlyList<TenantConfig> tenants, bool legacy = false)
    {
        var byApiKey        = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        var byInternalToken = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        var byAppId         = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        foreach (var t in tenants)
        {
            if (!byAppId.TryAdd(t.AppId, t))
                throw new InvalidOperationException($"duplicate tenant app_id '{t.AppId}'");
            if (string.IsNullOrEmpty(t.TenantApiKey))
                throw new InvalidOperationException($"tenant '{t.AppId}' has no tenant_api_key");
            if (!byApiKey.TryAdd(t.TenantApiKey, t))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}' shares a tenant_api_key with '{byApiKey[t.TenantApiKey].AppId}'");
            if (string.IsNullOrEmpty(t.InternalToken))
            {
                if (!legacy)
                    throw new InvalidOperationException($"tenant '{t.AppId}' has no internal_token");
                continue;
            }
            if (!byInternalToken.TryAdd(t.InternalToken, t))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}' shares an internal_token with '{byInternalToken[t.InternalToken].AppId}'");
            // Cross-check: the client key and the internal token must be
            // distinct. A shared value collapses the two-tier trust model
            // and would let a leaked SDK key hit /internal/v1/*.
            if (StringComparer.Ordinal.Equals(t.TenantApiKey, t.InternalToken))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}': tenant_api_key and internal_token must be different values");
        }
        // The same-tenant check above only catches a value reused inside one
        // file. The cross-tenant case is just as fatal and easier to hit —
        // two files hand-edited from the same REPLACE_ME_* template — because
        // ResolveInternalTenant() scans *every* tenant's internal_token: if
        // tenant A's client key is tenant B's server secret, then A's key,
        // which ships inside A's APK and web bundle, authenticates as B on
        // POST /internal/v1/events and the DSR erasure route.
        foreach (var (secret, client) in byApiKey)
            if (byInternalToken.TryGetValue(secret, out var server))
                throw new InvalidOperationException(
                    $"tenant '{client.AppId}': its tenant_api_key is also the internal_token of tenant "
                    + $"'{server.AppId}' — a client key ships in SDK bundles and must never authenticate "
                    + "on the internal listener");
        All = tenants;
    }

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
            var files = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".json", StringComparison.Ordinal)
                            || f.EndsWith(".jsonc", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
                throw new InvalidOperationException(
                    $"EP_TENANTS_DIR '{dir}' contains no tenant files (expected *.json or *.jsonc)");
            var tenants = files.Select(TenantConfig.Load).ToArray();
            return new TenantRegistry(tenants);
        }

        // Back-compat: build one tenant from legacy env.
        if (string.IsNullOrWhiteSpace(config.TrackingPlanPath))
            throw new InvalidOperationException(
                "EP_TENANTS_DIR is unset and EP_TRACKING_PLAN is not set — nothing to load");
        var plan = TrackingPlan.Load(config.TrackingPlanPath);
        return new TenantRegistry([TenantConfig.FromLegacyEnvironment(config, plan)], legacy: true);
    }

    /// <summary>Direct construction for tests.</summary>
    public static TenantRegistry ForTesting(params TenantConfig[] tenants)
        => new(tenants);
}
