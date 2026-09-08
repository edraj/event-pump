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
            ValidateDestinationCredentials(t);
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
    /// A destination switched on without the credentials its sender needs is
    /// a boot-time failure, not a delivery-time one. Left to run, a typo'd api
    /// key or a forgotten measurement_id starts a pipeline that fails every
    /// send: the tenant's outbox fills with `failed` rows behind
    /// circuit-breaker backoff, and the first anyone hears of it is hours of
    /// events sitting undelivered. The credential is knowable at boot, so it
    /// is checked at boot.
    ///
    /// What each destination requires is what its sender actually reads, no
    /// more: a field the sender treats as optional is not required here.
    /// Disabled destinations are skipped entirely, so a tenant file can carry
    /// a scaffolded Meta block with empty credentials and switch it on later.
    ///
    /// Erasure needs nothing extra. SenderFactory registers an erasure
    /// pipeline only alongside its vendor, so the vendor's own credentials
    /// cover it — and Amplitude's deletion-only `secret_key` stays a
    /// delivery-time `skipped: no_secret_key` on purpose (SPEC §9.7.2), since
    /// requiring it at boot would lock out every tenant that has never
    /// obtained one.
    /// </summary>
    private static void ValidateDestinationCredentials(TenantConfig t)
    {
        if (t.Ga4Enabled)
        {
            Require(t, "ga4", "api_secret", t.Ga4ApiSecret);
            // The Measurement Protocol keys a stream by one or the other, and
            // Ga4Sender picks per identity handle (web client id vs Firebase
            // app instance id). Neither set means every send is a 400.
            if (string.IsNullOrEmpty(t.Ga4MeasurementId) && string.IsNullOrEmpty(t.Ga4FirebaseAppId))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}': ga4 enabled but neither measurement_id nor "
                    + "firebase_app_id is set");
        }
        if (t.AmplitudeEnabled)
            Require(t, "amplitude", "api_key", t.AmplitudeApiKey);
        if (t.MoEngageEnabled)
        {
            Require(t, "moengage", "moengage_app_id", t.MoEngageAppId);
            Require(t, "moengage", "api_key", t.MoEngageApiKey);
        }
        if (t.AdjustEnabled)
            Require(t, "adjust", "app_token", t.AdjustAppToken);
        if (t.MetaEnabled)
        {
            Require(t, "meta", "pixel_id", t.MetaPixelId);
            Require(t, "meta", "access_token", t.MetaAccessToken);
        }

        static void Require(TenantConfig tenant, string destination, string field, string value)
        {
            if (value.Length == 0)
                throw new InvalidOperationException(
                    $"tenant '{tenant.AppId}': {destination} enabled but {field} is empty");
        }
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
