namespace EventPump.Config;

/// <summary>Process configuration (SPEC §13). Env-var driven; no reflection binding.</summary>
public sealed record EpConfig
{
    public required string DbConnString { get; init; }
    public string Listen { get; init; } = "http://127.0.0.1:8080";
    public string InternalListen { get; init; } = "http://127.0.0.1:8081";
    /// <summary>token -> app_id</summary>
    public Dictionary<string, string> ClientTokens { get; init; } = [];
    public string InternalToken { get; init; } = "";
    public string? CookieDomain { get; init; }
    public string[] CorsOrigins { get; init; } = [];
    public int RateLimitPermits { get; init; } = 600;
    public int RateLimitWindowSeconds { get; init; } = 60;
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
    public int SenderTimeoutMs { get; init; } = 10_000;

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
        var rate = Optional("EP_RATE_LIMIT") ?? "600/60";
        var slash = rate.IndexOf('/');
        if (slash <= 0
            || !int.TryParse(rate[..slash], out var permits)
            || !int.TryParse(rate[(slash + 1)..], out var windowSeconds))
        {
            throw new InvalidOperationException("EP_RATE_LIMIT must be <permits>/<window_seconds>");
        }

        return new EpConfig
        {
            DbConnString = Required("EP_DB_CONNSTRING"),
            Listen = Optional("EP_LISTEN") ?? "http://127.0.0.1:8080",
            InternalListen = Optional("EP_INTERNAL_LISTEN") ?? "http://127.0.0.1:8081",
            ClientTokens = ParseClientTokens(Optional("EP_CLIENT_TOKENS") ?? ""),
            InternalToken = Optional("EP_INTERNAL_TOKEN") ?? "",
            CookieDomain = Optional("EP_COOKIE_DOMAIN"),
            CorsOrigins = (Optional("EP_CORS_ORIGINS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RateLimitPermits = permits,
            RateLimitWindowSeconds = windowSeconds,
            TrackingPlanPath = Required("EP_TRACKING_PLAN"),
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
            SenderTimeoutMs = int.Parse(Optional("EP_SENDER_TIMEOUT_MS") ?? "10000"),
            Ga4Enabled = Optional("EP_GA4_ENABLED") == "true",
            Ga4Endpoint = Optional("EP_GA4_ENDPOINT") ?? "https://www.google-analytics.com",
            Ga4ApiSecret = Optional("EP_GA4_API_SECRET") ?? "",
            Ga4MeasurementId = Optional("EP_GA4_MEASUREMENT_ID"),
            Ga4FirebaseAppId = Optional("EP_GA4_FIREBASE_APP_ID"),
            AmplitudeEnabled = Optional("EP_AMPLITUDE_ENABLED") == "true",
            AmplitudeEndpoint = Optional("EP_AMPLITUDE_ENDPOINT") ?? "https://api2.amplitude.com/2/httpapi",
            AmplitudeApiKey = Optional("EP_AMPLITUDE_API_KEY") ?? "",
            MoEngageEnabled = Optional("EP_MOENGAGE_ENABLED") == "true",
            MoEngageEndpoint = Optional("EP_MOENGAGE_ENDPOINT") ?? "https://api-01.moengage.com",
            MoEngageAppId = Optional("EP_MOENGAGE_APP_ID") ?? "",
            MoEngageApiKey = Optional("EP_MOENGAGE_API_KEY") ?? "",
            AdjustEnabled = Optional("EP_ADJUST_ENABLED") == "true",
            AdjustEndpoint = Optional("EP_ADJUST_ENDPOINT") ?? "https://s2s.adjust.com/event",
            AdjustAppToken = Optional("EP_ADJUST_APP_TOKEN") ?? "",
            AdjustS2sToken = Optional("EP_ADJUST_S2S_TOKEN"),
            MetaEnabled = Optional("EP_META_ENABLED") == "true",
            MetaEndpoint = Optional("EP_META_ENDPOINT") ?? "https://graph.facebook.com",
            MetaGraphVersion = Optional("EP_META_GRAPH_VERSION") ?? "v25.0",
            MetaPixelId = Optional("EP_META_PIXEL_ID") ?? "",
            MetaAccessToken = Optional("EP_META_ACCESS_TOKEN") ?? "",
            MetaTestEventCode = Optional("EP_META_TEST_EVENT_CODE"),
            MetaConsentGating = Optional("EP_META_CONSENT_GATING") == "true",
            MetaActionSource = Optional("EP_META_ACTION_SOURCE") ?? "website",
        };
    }

    private static Dictionary<string, string> ParseClientTokens(string spec)
    {
        // EP_CLIENT_TOKENS=app_id:token[,app_id:token...]
        var tokens = new Dictionary<string, string>();
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0 || colon == pair.Length - 1)
                throw new InvalidOperationException("EP_CLIENT_TOKENS must be app_id:token[,app_id:token...]");
            tokens[pair[(colon + 1)..]] = pair[..colon];
        }
        return tokens;
    }

    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required");

    private static string? Optional(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
