using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EventPump.Config;

/// <summary>
/// Per-tenant configuration (SPEC v1.2 §13.2). One file per tenant in
/// EP_TENANTS_DIR; each file is JSONC (comments allowed). A file that fails
/// validation aborts boot — silent drop of a tenant would let its events
/// pile up as unauthorised rejections in the wrong bucket.
/// </summary>
public sealed record TenantConfig
{
    public required string AppId { get; init; }

    /// <summary>Bearer tokens that identify this tenant on POST /v1/*.</summary>
    public string[] ClientTokens { get; init; } = [];

    /// <summary>Shared secret for POST /internal/v1/*.</summary>
    public string InternalToken { get; init; } = "";

    /// <summary>Cookie Domain for ep_aid (SPEC §9.5). Null → host-only.</summary>
    public string? CookieDomain { get; init; }

    /// <summary>Allowed browser origins for POST /v1/*.</summary>
    public string[] CorsOrigins { get; init; } = [];

    public int RateLimitPermits { get; init; } = 600;
    public int RateLimitWindowSeconds { get; init; } = 60;
    public int ErrorRateLimitPermits { get; init; } = 120;
    public int ErrorRateLimitWindowSeconds { get; init; } = 60;

    /// <summary>This tenant's tracking plan (SPEC §6.1, §6.2, §8).</summary>
    public required TrackingPlan Plan { get; init; }

    // Per-destination attribute gates (SPEC §6.1). Attribute-free events
    // still flow when the gate is off — only attribute-derived fields drop.
    public bool Ga4AttributesEnabled { get; init; }
    public bool AmplitudeAttributesEnabled { get; init; }
    public bool MoEngageAttributesEnabled { get; init; } = true;
    public bool AdjustAttributesEnabled { get; init; }
    public bool MetaAttributesEnabled { get; init; }

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

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses a per-tenant JSONC file. `path` is used only for diagnostic
    /// messages. Throws InvalidDataException on any structural or semantic
    /// issue (missing app_id, malformed tracking plan, empty tokens, …) —
    /// deliberately loud, since a broken tenant file should stop the boot.
    /// </summary>
    public static TenantConfig Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Same as Load but takes the raw text (used by tests).</summary>
    public static TenantConfig Parse(string raw, string sourceLabel = "<inline>")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw, DocOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"tenant file '{sourceLabel}': malformed JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"tenant file '{sourceLabel}': root must be an object");

            var appId = RequiredString(root, "app_id", sourceLabel);
            var clientTokens = RequiredStringArray(root, "client_tokens", sourceLabel);
            if (clientTokens.Length == 0)
                throw new InvalidDataException($"tenant file '{sourceLabel}': client_tokens must be non-empty");
            var internalToken = RequiredString(root, "internal_token", sourceLabel);

            // Reassemble the plan sub-tree into a JSON document that
            // TrackingPlan.Parse understands, so all plan validation stays in
            // one place. Missing sub-blocks are written as empty objects.
            var planJson = BuildPlanJson(root);
            TrackingPlan plan;
            try
            {
                plan = TrackingPlan.Parse(planJson);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"tenant file '{sourceLabel}': tracking plan invalid: {ex.Message}", ex);
            }

            var rate = SubObject(root, "rate_limit");
            var errRate = SubObject(root, "error_rate_limit");
            var dest = SubObject(root, "destination_config");
            var ga4 = SubObject(dest, "ga4");
            var amp = SubObject(dest, "amplitude");
            var moe = SubObject(dest, "moengage");
            var adj = SubObject(dest, "adjust");
            var meta = SubObject(dest, "meta");

            return new TenantConfig
            {
                AppId = appId,
                ClientTokens = clientTokens,
                InternalToken = internalToken,
                CookieDomain = OptionalString(root, "cookie_domain"),
                CorsOrigins = OptionalStringArray(root, "cors_origins") ?? [],
                RateLimitPermits = OptionalInt(rate, "permits") ?? 600,
                RateLimitWindowSeconds = OptionalInt(rate, "window_seconds") ?? 60,
                ErrorRateLimitPermits = OptionalInt(errRate, "permits") ?? 120,
                ErrorRateLimitWindowSeconds = OptionalInt(errRate, "window_seconds") ?? 60,
                Plan = plan,

                Ga4Enabled = OptionalBool(ga4, "enabled") ?? false,
                Ga4Endpoint = OptionalString(ga4, "endpoint") ?? "https://www.google-analytics.com",
                Ga4ApiSecret = OptionalString(ga4, "api_secret") ?? "",
                Ga4MeasurementId = OptionalString(ga4, "measurement_id"),
                Ga4FirebaseAppId = OptionalString(ga4, "firebase_app_id"),
                Ga4AttributesEnabled = OptionalBool(ga4, "attributes_enabled") ?? false,

                AmplitudeEnabled = OptionalBool(amp, "enabled") ?? false,
                AmplitudeEndpoint = OptionalString(amp, "endpoint") ?? "https://api2.amplitude.com/2/httpapi",
                AmplitudeApiKey = OptionalString(amp, "api_key") ?? "",
                AmplitudeAttributesEnabled = OptionalBool(amp, "attributes_enabled") ?? false,

                MoEngageEnabled = OptionalBool(moe, "enabled") ?? false,
                MoEngageEndpoint = OptionalString(moe, "endpoint") ?? "https://api-01.moengage.com",
                MoEngageAppId = OptionalString(moe, "moengage_app_id") ?? "",
                MoEngageApiKey = OptionalString(moe, "api_key") ?? "",
                MoEngageAttributesEnabled = OptionalBool(moe, "attributes_enabled") ?? true,

                AdjustEnabled = OptionalBool(adj, "enabled") ?? false,
                AdjustEndpoint = OptionalString(adj, "endpoint") ?? "https://s2s.adjust.com/event",
                AdjustAppToken = OptionalString(adj, "app_token") ?? "",
                AdjustS2sToken = OptionalString(adj, "s2s_token"),
                AdjustAttributesEnabled = OptionalBool(adj, "attributes_enabled") ?? false,

                MetaEnabled = OptionalBool(meta, "enabled") ?? false,
                MetaEndpoint = OptionalString(meta, "endpoint") ?? "https://graph.facebook.com",
                MetaGraphVersion = OptionalString(meta, "graph_version") ?? "v25.0",
                MetaPixelId = OptionalString(meta, "pixel_id") ?? "",
                MetaAccessToken = OptionalString(meta, "access_token") ?? "",
                MetaTestEventCode = OptionalString(meta, "test_event_code"),
                MetaConsentGating = OptionalBool(meta, "consent_gating") ?? false,
                MetaActionSource = OptionalString(meta, "action_source") ?? "website",
                MetaAttributesEnabled = OptionalBool(meta, "attributes_enabled") ?? false,
            };
        }
    }

    /// <summary>
    /// Back-compat path (SPEC v1.2 §13.4): when EP_TENANTS_DIR is unset,
    /// synthesise a single tenant from the pre-v1.2 EP_* env vars and the
    /// file at EP_TRACKING_PLAN. Existing single-tenant deployments keep
    /// working without touching config; adding a second tenant is what
    /// forces the move to EP_TENANTS_DIR.
    /// </summary>
    public static TenantConfig FromLegacyEnvironment(EpConfig config, TrackingPlan plan)
    {
        // Every legacy token must resolve to a single app_id, otherwise the
        // legacy env is expressing a multi-tenant deployment that must move
        // to EP_TENANTS_DIR (we cannot invent the extra per-tenant knobs).
        var appIds = config.ClientTokens.Values.Distinct().ToArray();
        if (appIds.Length > 1)
            throw new InvalidOperationException(
                "EP_CLIENT_TOKENS binds tokens to multiple app_ids; use EP_TENANTS_DIR " +
                "with one JSON file per tenant instead.");
        var appId = appIds.Length == 1 ? appIds[0] : "zainmart";

        return new TenantConfig
        {
            AppId = appId,
            ClientTokens = config.ClientTokens.Keys.ToArray(),
            InternalToken = config.InternalToken,
            CookieDomain = config.CookieDomain,
            CorsOrigins = config.CorsOrigins,
            RateLimitPermits = config.RateLimitPermits,
            RateLimitWindowSeconds = config.RateLimitWindowSeconds,
            ErrorRateLimitPermits = config.ErrorRateLimitPermits,
            ErrorRateLimitWindowSeconds = config.ErrorRateLimitWindowSeconds,
            Plan = plan,

            Ga4Enabled = config.Ga4Enabled,
            Ga4Endpoint = config.Ga4Endpoint,
            Ga4ApiSecret = config.Ga4ApiSecret,
            Ga4MeasurementId = config.Ga4MeasurementId,
            Ga4FirebaseAppId = config.Ga4FirebaseAppId,
            Ga4AttributesEnabled = config.Ga4AttributesEnabled,

            AmplitudeEnabled = config.AmplitudeEnabled,
            AmplitudeEndpoint = config.AmplitudeEndpoint,
            AmplitudeApiKey = config.AmplitudeApiKey,
            AmplitudeAttributesEnabled = config.AmplitudeAttributesEnabled,

            MoEngageEnabled = config.MoEngageEnabled,
            MoEngageEndpoint = config.MoEngageEndpoint,
            MoEngageAppId = config.MoEngageAppId,
            MoEngageApiKey = config.MoEngageApiKey,
            MoEngageAttributesEnabled = config.MoEngageAttributesEnabled,

            AdjustEnabled = config.AdjustEnabled,
            AdjustEndpoint = config.AdjustEndpoint,
            AdjustAppToken = config.AdjustAppToken,
            AdjustS2sToken = config.AdjustS2sToken,
            AdjustAttributesEnabled = config.AdjustAttributesEnabled,

            MetaEnabled = config.MetaEnabled,
            MetaEndpoint = config.MetaEndpoint,
            MetaGraphVersion = config.MetaGraphVersion,
            MetaPixelId = config.MetaPixelId,
            MetaAccessToken = config.MetaAccessToken,
            MetaTestEventCode = config.MetaTestEventCode,
            MetaConsentGating = config.MetaConsentGating,
            MetaActionSource = config.MetaActionSource,
            MetaAttributesEnabled = config.MetaAttributesEnabled,
        };
    }

    // -------------------------------------------------------------- helpers

    private static string BuildPlanJson(JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteSubObject(writer, root, "attributes");
            WriteSubObject(writer, root, "events");
            WriteSubObject(writer, root, "destinations");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSubObject(Utf8JsonWriter writer, JsonElement parent, string name)
    {
        writer.WritePropertyName(name);
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var sub))
            sub.WriteTo(writer);
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private static JsonElement SubObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var sub)
           && sub.ValueKind == JsonValueKind.Object
            ? sub
            : default;

    private static string RequiredString(JsonElement root, string name, string sourceLabel)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && v.GetString() is { Length: > 0 } s
            ? s
            : throw new InvalidDataException($"tenant file '{sourceLabel}': {name} is required");

    private static string[] RequiredStringArray(JsonElement root, string name, string sourceLabel)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"tenant file '{sourceLabel}': {name} must be an array of strings");
        var items = new List<string>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 } s)
                throw new InvalidDataException($"tenant file '{sourceLabel}': {name} entries must be non-empty strings");
            items.Add(s);
        }
        return items.ToArray();
    }

    private static string? OptionalString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string[]? OptionalStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out var arr)
            || arr.ValueKind != JsonValueKind.Array) return null;
        var items = new List<string>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s) items.Add(s);
        return items.ToArray();
    }

    private static int? OptionalInt(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i)
            ? i
            : null;

    private static bool? OptionalBool(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
}
