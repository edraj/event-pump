namespace EventPump.Config;

/// <summary>
/// Runtime index of all tenants (SPEC v1.2 §13.2 / §13.4). Built once at
/// process boot; immutable thereafter. Token → tenant is the auth hot path,
/// so it is a plain Dictionary lookup.
/// </summary>
public sealed class TenantRegistry
{
    private readonly Dictionary<string, TenantConfig> _byApiKey;
    private readonly Dictionary<string, TenantConfig> _byInternalToken;
    private readonly Dictionary<string, TenantConfig> _byAppId;

    public IReadOnlyCollection<TenantConfig> All { get; }

    private TenantRegistry(IReadOnlyList<TenantConfig> tenants)
    {
        _byApiKey        = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        _byInternalToken = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        _byAppId         = new Dictionary<string, TenantConfig>(StringComparer.Ordinal);
        foreach (var t in tenants)
        {
            if (!_byAppId.TryAdd(t.AppId, t))
                throw new InvalidOperationException($"duplicate tenant app_id '{t.AppId}'");
            if (string.IsNullOrEmpty(t.TenantApiKey))
                throw new InvalidOperationException($"tenant '{t.AppId}' has no tenant_api_key");
            if (!_byApiKey.TryAdd(t.TenantApiKey, t))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}' shares a tenant_api_key with '{_byApiKey[t.TenantApiKey].AppId}'");
            if (string.IsNullOrEmpty(t.InternalToken))
                throw new InvalidOperationException($"tenant '{t.AppId}' has no internal_token");
            if (!_byInternalToken.TryAdd(t.InternalToken, t))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}' shares an internal_token with '{_byInternalToken[t.InternalToken].AppId}'");
            // Cross-check: the client key and the internal token must be
            // distinct. A shared value collapses the two-tier trust model
            // and would let a leaked SDK key hit /internal/v1/*.
            if (StringComparer.Ordinal.Equals(t.TenantApiKey, t.InternalToken))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}': tenant_api_key and internal_token must be different values");
            ValidateDestinationCredentials(t);
        }
        All = tenants;
    }

    /// <summary>
    /// Fail loud at boot when a destination is enabled but missing the
    /// credentials the sender will need at delivery time. Without this,
    /// a typo'd api key or a forgotten measurement_id boots cleanly and
    /// only surfaces once events start failing hours later — with the
    /// tenant's outbox filling up in the meantime.
    /// </summary>
    private static void ValidateDestinationCredentials(TenantConfig t)
    {
        static void Require(string appId, string destination, string field, string value)
        {
            if (value.Length == 0)
                throw new InvalidOperationException(
                    $"tenant '{appId}': {destination} enabled but {field} is empty");
        }

        if (t.Ga4Enabled)
        {
            Require(t.AppId, "ga4", "api_secret", t.Ga4ApiSecret);
            // GA4 Measurement Protocol accepts either a web measurement_id
            // OR a firebase_app_id (Ga4Sender.cs picks per identity handle);
            // at least one must be set or every send fails with 400.
            if (string.IsNullOrEmpty(t.Ga4MeasurementId) && string.IsNullOrEmpty(t.Ga4FirebaseAppId))
                throw new InvalidOperationException(
                    $"tenant '{t.AppId}': ga4 enabled but neither measurement_id nor firebase_app_id is set");
        }
        if (t.AmplitudeEnabled)
            Require(t.AppId, "amplitude", "api_key", t.AmplitudeApiKey);
        if (t.MoEngageEnabled)
        {
            Require(t.AppId, "moengage", "moengage_app_id", t.MoEngageAppId);
            Require(t.AppId, "moengage", "api_key", t.MoEngageApiKey);
        }
        if (t.AdjustEnabled)
            Require(t.AppId, "adjust", "app_token", t.AdjustAppToken);
        if (t.MetaEnabled)
        {
            Require(t.AppId, "meta", "pixel_id", t.MetaPixelId);
            Require(t.AppId, "meta", "access_token", t.MetaAccessToken);
        }
    }

    /// <summary>
    /// SPEC §9.1: match a bearer against every tenant's client `tenant_api_key`.
    /// Used only on the public listener (POST /v1/*). Returns null when no
    /// tenant claims it (401 unauthorized).
    /// </summary>
    public TenantConfig? ByApiKey(string apiKey) => _byApiKey.GetValueOrDefault(apiKey);

    /// <summary>
    /// SPEC §9.3: match a bearer against every tenant's `internal_token`.
    /// Used only on the internal listener (POST /internal/v1/* and DSR).
    /// A client key deliberately does NOT resolve here — the two secrets
    /// are separate for a reason.
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
        return new TenantRegistry([TenantConfig.FromLegacyEnvironment(config, plan)]);
    }

    /// <summary>Direct construction for tests.</summary>
    public static TenantRegistry ForTesting(params TenantConfig[] tenants)
        => new(tenants);
}
