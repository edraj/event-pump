namespace EventPump.Config;

/// <summary>Process configuration (SPEC §13). Env-var driven; no reflection binding.</summary>
public sealed record EpConfig
{
    public required string DbConnString { get; init; }
    public string Listen { get; init; } = "http://127.0.0.1:8080";
    public string InternalListen { get; init; } = "http://127.0.0.1:8081";
    /// <summary>
    /// Client-side per-tenant API key (SPEC v1.2 §9.1). Ships in the mobile /
    /// web SDK bundle and authenticates POST /v1/*. Only used when
    /// EP_TENANTS_DIR is unset (legacy single-tenant path).
    /// </summary>
    public string TenantApiKey { get; init; } = "";
    /// <summary>
    /// Server-side per-tenant secret (SPEC v1.2 §9.3). Authenticates POST
    /// /internal/v1/events and DSR DELETE. Kept out of any client bundle.
    /// Only used when EP_TENANTS_DIR is unset (legacy single-tenant path).
    /// </summary>
    public string InternalToken { get; init; } = "";
    /// <summary>Synthesised tenant's app_id for the legacy env path.</summary>
    public string LegacyAppId { get; init; } = "zainmart";
    public string? CookieDomain { get; init; }
    public string[] CorsOrigins { get; init; } = [];

    public string[] TrustedProxies { get; init; } = ["127.0.0.1/32", "::1/128"];
    public int RateLimitPermits { get; init; } = 600;
    public int RateLimitWindowSeconds { get; init; } = 60;
    /// <summary>Separate bucket: an error storm must never throttle product events.</summary>
    public int ErrorRateLimitPermits { get; init; } = 120;
    public int ErrorRateLimitWindowSeconds { get; init; } = 60;
    /// <summary>Hard ceiling on the /internal/v1/query window (aligns with partitions).</summary>
    public int QueryMaxDays { get; init; } = 5;
    /// <summary>
    /// Where /docs answers: <c>both</c> listeners (default), <c>internal</c>
    /// only, or <c>off</c>. The spec lists every route the app maps, /internal/*
    /// included, so an internet-facing deployment may prefer not to publish it.
    /// Note that eventpump.env is %config(noreplace): an upgraded install keeps
    /// its own file and never picks up a new line from .env.example, so a
    /// deployment that wants `internal` has to set it by hand.
    /// </summary>
    public string Docs { get; init; } = "both";
    public string TrackingPlanPath { get; init; } = "";
    public string IpMode { get; init; } = "raw";
    public int RetentionDays { get; init; } = 30;
    public int RetentionDeadDays { get; init; } = 90;
    public string MetricsListen { get; init; } = "http://127.0.0.1:9090";
    public int WorkerPollMs { get; init; } = 1000;
    public int ClaimBatchSize { get; init; } = 50;
    public int SendConcurrency { get; init; } = 4;
    public double BackoffBaseSeconds { get; init; } = 30;
    public double BackoffCapSeconds { get; init; } = 3600;
    public int MaxAttempts { get; init; } = 10;
    public int BreakerThreshold { get; init; } = 5;
    public int BreakerPauseSeconds { get; init; } = 120;
    public int LeaseSeconds { get; init; } = 300;
    public int IdentityGraceSeconds { get; init; } = 300;
    public int SenderTimeoutMs { get; init; } = 10_000;

    // Per-destination user-attribute gates (SPEC §6.1 / §13). When OFF, the
    // sender still delivers the event core; only attribute-derived fields are
    // omitted. MoEngage defaults ON — it is the designated raw-PII destination.
    public bool Ga4AttributesEnabled { get; init; }
    public bool AmplitudeAttributesEnabled { get; init; }
    public bool MoEngageAttributesEnabled { get; init; } = true;
    public bool AdjustAttributesEnabled { get; init; }
    public bool MetaAttributesEnabled { get; init; }

    public bool MoEngageErasureEnabled { get; init; } = true;
    public bool AdjustErasureEnabled { get; init; } = true;
    public bool AmplitudeErasureEnabled { get; init; } = true;
    public bool Ga4ErasureEnabled { get; init; } = true;

    // GA4 Measurement Protocol
    public bool Ga4Enabled { get; init; }
    public string Ga4Endpoint { get; init; } = "https://www.google-analytics.com";
    public string Ga4ApiSecret { get; init; } = "";
    public string? Ga4MeasurementId { get; init; }
    public string? Ga4FirebaseAppId { get; init; }

    // Amplitude HTTP V2
    public bool AmplitudeEnabled { get; init; }
    public string AmplitudeEndpoint { get; init; } = "https://api2.amplitude.com/2/httpapi";
    public string AmplitudeApiKey { get; init; } = "";
    public string AmplitudeSecretKey { get; init; } = "";
    public string AmplitudeErasureEndpoint { get; init; } =
        "https://amplitude.com/api/2/deletions/users";

    // MoEngage Data API
    public bool MoEngageEnabled { get; init; }
    public string MoEngageEndpoint { get; init; } = "https://api-01.moengage.com";
    public string MoEngageAppId { get; init; } = "";
    public string MoEngageApiKey { get; init; } = "";

    // Adjust S2S
    public bool AdjustEnabled { get; init; }
    public string AdjustEndpoint { get; init; } = "https://s2s.adjust.com/event";
    public string AdjustAppToken { get; init; } = "";
    public string? AdjustS2sToken { get; init; }
    public string AdjustErasureEndpoint { get; init; } =
        "https://gdpr.adjust.com/gdpr_forget_device";

    // Meta CAPI (reference subclass; disabled by default per SPEC §12)
    public bool MetaEnabled { get; init; }
    public string MetaEndpoint { get; init; } = "https://graph.facebook.com";
    public string MetaGraphVersion { get; init; } = "v25.0";
    public string MetaPixelId { get; init; } = "";
    public string MetaAccessToken { get; init; } = "";
    public string? MetaTestEventCode { get; init; }
    public bool MetaConsentGating { get; init; }
    public string MetaActionSource { get; init; } = "website";

    public static EpConfig FromEnvironment()
    {
        RejectRetiredVars();
        var (permits, windowSeconds) = ParseRate("EP_RATE_LIMIT", "600/60");
        var (errorPermits, errorWindowSeconds) = ParseRate("EP_ERROR_RATE_LIMIT", "120/60");

        return new EpConfig
        {
            DbConnString = Required("EP_DB_CONNSTRING"),
            Listen = Optional("EP_LISTEN") ?? "http://127.0.0.1:8080",
            InternalListen = Optional("EP_INTERNAL_LISTEN") ?? "http://127.0.0.1:8081",
            TenantApiKey = Optional("EP_TENANT_API_KEY") ?? "",
            InternalToken = Optional("EP_INTERNAL_TOKEN") ?? "",
            LegacyAppId  = Optional("EP_LEGACY_APP_ID") ?? "zainmart",
            CookieDomain = Optional("EP_COOKIE_DOMAIN"),
            CorsOrigins = (Optional("EP_CORS_ORIGINS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TrustedProxies = (Optional("EP_TRUSTED_PROXIES") ?? "127.0.0.1/32,::1/128")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RateLimitPermits = permits,
            RateLimitWindowSeconds = windowSeconds,
            ErrorRateLimitPermits = errorPermits,
            ErrorRateLimitWindowSeconds = errorWindowSeconds,
            QueryMaxDays = int.Parse(Optional("EP_QUERY_MAX_DAYS") ?? "5"),
            Docs = ParseDocs(Optional("EP_DOCS") ?? "both"),
            // EP_TRACKING_PLAN is required only for the pre-v1.2 back-compat
            // path (no EP_TENANTS_DIR); when tenants live in files the plan
            // travels with each tenant.
            TrackingPlanPath = Environment.GetEnvironmentVariable("EP_TENANTS_DIR") is { Length: > 0 }
                ? (Optional("EP_TRACKING_PLAN") ?? "")
                : Required("EP_TRACKING_PLAN"),
            IpMode = Optional("EP_IP_MODE") ?? "raw",
            RetentionDays = int.Parse(Optional("EP_RETENTION_DAYS") ?? "30"),
            RetentionDeadDays = int.Parse(Optional("EP_RETENTION_DEAD_DAYS") ?? "90"),
            MetricsListen = Optional("EP_METRICS_LISTEN") ?? "http://127.0.0.1:9090",
            WorkerPollMs = int.Parse(Optional("EP_WORKER_POLL_MS") ?? "1000"),
            ClaimBatchSize = int.Parse(Optional("EP_WORKER_CLAIM_BATCH") ?? "50"),
            SendConcurrency = int.Parse(Optional("EP_WORKER_SEND_CONCURRENCY") ?? "4"),
            BackoffBaseSeconds = double.Parse(Optional("EP_WORKER_BACKOFF_BASE_S") ?? "30"),
            BackoffCapSeconds = double.Parse(Optional("EP_WORKER_BACKOFF_CAP_S") ?? "3600"),
            MaxAttempts = int.Parse(Optional("EP_WORKER_MAX_ATTEMPTS") ?? "10"),
            BreakerThreshold = int.Parse(Optional("EP_WORKER_BREAKER_THRESHOLD") ?? "5"),
            BreakerPauseSeconds = int.Parse(Optional("EP_WORKER_BREAKER_PAUSE_S") ?? "120"),
            LeaseSeconds = int.Parse(Optional("EP_WORKER_LEASE_S") ?? "300"),
            IdentityGraceSeconds = int.Parse(Optional("EP_IDENTITY_GRACE_S") ?? "300"),
            SenderTimeoutMs = int.Parse(Optional("EP_SENDER_TIMEOUT_MS") ?? "10000"),
            Ga4AttributesEnabled = Flag("EP_GA4_ATTRIBUTES_ENABLED", false),
            AmplitudeAttributesEnabled = Flag("EP_AMPLITUDE_ATTRIBUTES_ENABLED", false),
            MoEngageAttributesEnabled = Flag("EP_MOENGAGE_ATTRIBUTES_ENABLED", true),
            AdjustAttributesEnabled = Flag("EP_ADJUST_ATTRIBUTES_ENABLED", false),
            MetaAttributesEnabled = Flag("EP_META_ATTRIBUTES_ENABLED", false),
            Ga4Enabled = Flag("EP_GA4_ENABLED", false),
            Ga4Endpoint = Optional("EP_GA4_ENDPOINT") ?? "https://www.google-analytics.com",
            Ga4ApiSecret = Optional("EP_GA4_API_SECRET") ?? "",
            Ga4MeasurementId = Optional("EP_GA4_MEASUREMENT_ID"),
            Ga4FirebaseAppId = Optional("EP_GA4_FIREBASE_APP_ID"),
            AmplitudeEnabled = Flag("EP_AMPLITUDE_ENABLED", false),
            AmplitudeEndpoint = Optional("EP_AMPLITUDE_ENDPOINT") ?? "https://api2.amplitude.com/2/httpapi",
            AmplitudeApiKey = Optional("EP_AMPLITUDE_API_KEY") ?? "",
            MoEngageEnabled = Flag("EP_MOENGAGE_ENABLED", false),
            MoEngageEndpoint = Optional("EP_MOENGAGE_ENDPOINT") ?? "https://api-01.moengage.com",
            MoEngageAppId = Optional("EP_MOENGAGE_APP_ID") ?? "",
            MoEngageApiKey = Optional("EP_MOENGAGE_API_KEY") ?? "",
            MoEngageErasureEnabled = Flag("EP_MOENGAGE_ERASURE_ENABLED", true),
            AdjustErasureEnabled = Flag("EP_ADJUST_ERASURE_ENABLED", true),
            AmplitudeErasureEnabled = Flag("EP_AMPLITUDE_ERASURE_ENABLED", true),
            Ga4ErasureEnabled = Flag("EP_GA4_ERASURE_ENABLED", true),
            AmplitudeSecretKey = Optional("EP_AMPLITUDE_SECRET_KEY") ?? "",
            AmplitudeErasureEndpoint = Optional("EP_AMPLITUDE_ERASURE_ENDPOINT")
                ?? "https://amplitude.com/api/2/deletions/users",
            AdjustErasureEndpoint = Optional("EP_ADJUST_ERASURE_ENDPOINT")
                ?? "https://gdpr.adjust.com/gdpr_forget_device",
            AdjustEnabled = Flag("EP_ADJUST_ENABLED", false),
            AdjustEndpoint = Optional("EP_ADJUST_ENDPOINT") ?? "https://s2s.adjust.com/event",
            AdjustAppToken = Optional("EP_ADJUST_APP_TOKEN") ?? "",
            AdjustS2sToken = Optional("EP_ADJUST_S2S_TOKEN"),
            MetaEnabled = Flag("EP_META_ENABLED", false),
            MetaEndpoint = Optional("EP_META_ENDPOINT") ?? "https://graph.facebook.com",
            MetaGraphVersion = Optional("EP_META_GRAPH_VERSION") ?? "v25.0",
            MetaPixelId = Optional("EP_META_PIXEL_ID") ?? "",
            MetaAccessToken = Optional("EP_META_ACCESS_TOKEN") ?? "",
            MetaTestEventCode = Optional("EP_META_TEST_EVENT_CODE"),
            MetaConsentGating = Flag("EP_META_CONSENT_GATING", false),
            MetaActionSource = Optional("EP_META_ACTION_SOURCE") ?? "website",
        };
    }

    /// <summary>
    /// Pre-v1.2 `EP_CLIENT_TOKENS=app_id:token[,app_id:token…]` mapped several
    /// app_ids onto one process. v1.2 replaced it with EP_TENANT_API_KEY (one
    /// tenant) or EP_TENANTS_DIR (many), and nothing reads it any more — so a
    /// deployment that still sets it would boot happily and quietly file every
    /// tenant's traffic under EP_LEGACY_APP_ID. That silently re-buckets
    /// `error_reports`, whose daily aggregation keys on (day, app_id,
    /// stack_hash), splitting each stack's history at the upgrade. Refuse to
    /// start instead, and say what to do about it.
    /// </summary>
    private static void RejectRetiredVars()
    {
        if (Optional("EP_CLIENT_TOKENS") is null) return;
        throw new InvalidOperationException(
            "EP_CLIENT_TOKENS was removed in v1.2 and is no longer read. Multi-app_id "
            + "deployments must move to EP_TENANTS_DIR (one file per tenant, see "
            + "deploy/tenants/README.md); a single-app_id deployment sets EP_TENANT_API_KEY "
            + "plus EP_LEGACY_APP_ID=<the app_id that was in EP_CLIENT_TOKENS>, and — if "
            + "backend producers post to /internal/v1/* or the DSR route — EP_INTERNAL_TOKEN, "
            + "which is a separate server-side secret and must not repeat EP_TENANT_API_KEY "
            + "(leave it unset to keep the internal listener closed). Leaving this "
            + "variable set would file every tenant's events under one app_id and split "
            + "error_reports aggregation at the upgrade. Unset it once migrated.");
    }

    private static (int Permits, int WindowSeconds) ParseRate(string name, string fallback)
    {
        var rate = Optional(name) ?? fallback;
        var slash = rate.IndexOf('/');
        if (slash <= 0
            || !int.TryParse(rate[..slash], out var permits)
            || !int.TryParse(rate[(slash + 1)..], out var windowSeconds))
        {
            throw new InvalidOperationException($"{name} must be <permits>/<window_seconds>");
        }
        return (permits, windowSeconds);
    }

    private static string ParseDocs(string value)
        => value is "both" or "internal" or "off"
            ? value
            : throw new InvalidOperationException("EP_DOCS must be both, internal or off");

    /// <summary>
    /// Env booleans, read the same way everywhere. The ad-hoc forms this
    /// replaced disagreed on what a value means: a default-off flag read as
    /// `== "true"` treated `EP_X=1` as off, and a default-on flag read as
    /// `!= "false"` treated `EP_X=False` and `EP_X=0` as on — so an operator
    /// switching a destination's erasure off got it left on, silently. An
    /// unrecognised value is a typo in a deployment, so it stops the process
    /// rather than resolving to whichever answer the default happened to be.
    /// </summary>
    private static bool Flag(string name, bool fallback)
    {
        if (Optional(name) is not { } raw) return fallback;
        var value = Truthy.Contains(raw) ? true
            : Falsy.Contains(raw) ? false
            : throw new InvalidOperationException(
                $"{name} must be one of true/false/1/0/yes/no/on/off, got '{raw}'");
        WarnIfSpelledAmbiguously(name, raw, value, fallback);
        return value;
    }

    /// <summary>
    /// Names a spelling the build before this one read the other way round.
    /// The ad-hoc reads this parser replaced were case-sensitive, so
    /// `EP_GA4_ENABLED=True` was off and is now on — a destination dark since
    /// install would start sending live traffic on this restart — and
    /// `EP_MOENGAGE_ATTRIBUTES_ENABLED=0` was on and is now off. Nothing in
    /// the deployment itself would report either.
    ///
    /// Whether this process is an upgrade or a first boot is not knowable
    /// here, so the line reports the ambiguity rather than asserting a
    /// history: on a fresh install nothing changed and it reads as a spelling
    /// note, on an upgraded one it is the only warning that a flag just
    /// flipped. Writing the value unambiguously settles it and silences the
    /// line, which is the point — the two readings should not go on being
    /// indistinguishable in a config file. Written to stderr because config is
    /// parsed before any logging is set up, and journald keeps it either way.
    /// </summary>
    private static void WarnIfSpelledAmbiguously(
        string name, string raw, bool value, bool fallback)
    {
        // The two idioms: default-off flags read `== "true"`, default-on flags
        // read `!= "false"`.
        var previously = fallback ? raw != "false" : raw == "true";
        if (previously == value) return;
        Console.Error.WriteLine(
            $"eventpump: {name}={raw} reads as {Word(value)}. Builds before this one read that "
            + $"spelling as {Word(previously)}, so if this deployment was upgraded the flag has "
            + $"just changed state. Write it `{Word(value)}` or `{Word(previously)}` to say "
            + "which you mean.");

        static string Word(bool on) => on ? "true" : "false";
    }

    private static readonly HashSet<string> Truthy =
        new(["true", "1", "yes", "on"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Falsy =
        new(["false", "0", "no", "off"], StringComparer.OrdinalIgnoreCase);

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required");

    private static string? Optional(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
