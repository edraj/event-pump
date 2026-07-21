using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using static EventPump.Data.EventStore;

namespace EventPump.Api;

/// <summary>Parses /v1/identity bodies (SPEC §9.2). Strict envelope, lenient handles.</summary>
public static class IdentityValidation
{
    private static readonly HashSet<string> TopLevelKeys =
        ["session_key", "anonymous_id", "session_number", "user_id", "first_seen_at", "handles", "attributes", "context"];

    // SPEC §6.1: serialized `attributes` object must not exceed this size.
    private const int MaxAttributesBytes = 4 * 1024;

    public sealed record ParsedAttributes(string AttributesJson, string Hash, bool IsEmpty);

    public static (IdentityUpsert? Identity, ParsedAttributes? Attributes, string? Error) Parse(
        JsonElement root, TrackingPlan plan)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null, "not_an_object");

        foreach (var property in root.EnumerateObject())
        {
            if (!TopLevelKeys.Contains(property.Name)) return (null, null, $"unknown_field:{property.Name}");
        }

        if (!TryUuid(root, "session_key", out var sessionKey) || sessionKey is null)
            return (null, null, "invalid_session_key");
        if (!TryUuid(root, "anonymous_id", out var anonymousId) || anonymousId is null)
            return (null, null, "invalid_anonymous_id");

        int? sessionNumber = null;
        if (root.TryGetProperty("session_number", out var num) && num.ValueKind != JsonValueKind.Null)
        {
            if (num.ValueKind != JsonValueKind.Number || !num.TryGetInt32(out var parsed) || parsed < 1)
                return (null, null, "invalid_session_number");
            sessionNumber = parsed;
        }

        string? userId = null;
        if (root.TryGetProperty("user_id", out var user) && user.ValueKind != JsonValueKind.Null)
        {
            if (user.ValueKind != JsonValueKind.String || user.GetString() is not { Length: > 0 and <= 256 } uid)
                return (null, null, "invalid_user_id");
            userId = uid;
        }

        string? clickIdsJson = null;
        string? ga4ClientId = null, ga4SessionId = null, firebaseAppInstanceId = null;
        string? amplitudeDeviceId = null, adjustAdid = null, adjustPlatformAdId = null, fbp = null, fbc = null;
        if (root.TryGetProperty("handles", out var handles) && handles.ValueKind != JsonValueKind.Null)
        {
            if (handles.ValueKind != JsonValueKind.Object) return (null, null, "invalid_handles");
            foreach (var handle in handles.EnumerateObject())
            {
                if (handle.Name == "click_ids")
                {
                    if (handle.Value.ValueKind != JsonValueKind.Object) return (null, null, "invalid_click_ids");
                    clickIdsJson = handle.Value.GetRawText();
                    continue;
                }
                if (handle.Value.ValueKind == JsonValueKind.Null) continue;
                if (handle.Value.ValueKind != JsonValueKind.String
                    || handle.Value.GetString() is not { Length: > 0 and <= 512 } value)
                    return (null, null, $"invalid_handle:{handle.Name}");
                switch (handle.Name)
                {
                    case "ga4_client_id": ga4ClientId = value; break;
                    case "ga4_session_id": ga4SessionId = value; break;
                    case "firebase_app_instance_id": firebaseAppInstanceId = value; break;
                    case "amplitude_device_id": amplitudeDeviceId = value; break;
                    case "adjust_adid": adjustAdid = value; break;
                    case "adjust_platform_ad_id": adjustPlatformAdId = value; break;
                    case "fbp": fbp = value; break;
                    case "fbc": fbc = value; break;
                    default: break; // unknown handles silently dropped (forward compat)
                }
            }
        }

        string? contextJson = null;
        if (root.TryGetProperty("context", out var context) && context.ValueKind != JsonValueKind.Null)
        {
            if (context.ValueKind != JsonValueKind.Object) return (null, null, "invalid_context");
            contextJson = context.GetRawText();
        }

        ParsedAttributes? attributes = null;
        if (root.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind != JsonValueKind.Null)
        {
            if (attrEl.ValueKind != JsonValueKind.Object) return (null, null, "invalid_attributes");
            var (parsed, error) = ParseAttributes(attrEl, plan);
            if (parsed is null) return (null, null, error);
            attributes = parsed;
        }

        var identity = new IdentityUpsert(
            sessionKey.Value, anonymousId.Value, sessionNumber, userId,
            ga4ClientId, ga4SessionId, firebaseAppInstanceId, amplitudeDeviceId,
            adjustAdid, adjustPlatformAdId, fbp, fbc, clickIdsJson, contextJson);
        return (identity, attributes, null);
    }

    /// <summary>
    /// Validates and normalizes each provided attribute against the tracking-plan
    /// allowlist (SPEC §6.1). Writes a canonical JSON with keys sorted
    /// alphabetically so `hash` is stable across identical payloads regardless
    /// of key order. `null` values pass through as JSON null — the storage layer
    /// interprets them as "clear this key".
    /// </summary>
    private static (ParsedAttributes?, string?) ParseAttributes(JsonElement el, TrackingPlan plan)
    {
        var sortedKeys = new List<string>();
        foreach (var property in el.EnumerateObject()) sortedKeys.Add(property.Name);
        sortedKeys.Sort(StringComparer.Ordinal);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var name in sortedKeys)
            {
                if (!plan.Attributes.TryGetValue(name, out var def))
                    return (null, $"unknown_attribute:{name}");

                var value = el.GetProperty(name);
                if (value.ValueKind == JsonValueKind.Null)
                {
                    writer.WriteNull(name);
                    continue;
                }
                if (value.ValueKind != JsonValueKind.String)
                    return (null, $"invalid_attribute:{name}");

                if (!TryNormalize(def, value.GetString()!, out var normalized))
                    return (null, $"invalid_attribute:{name}");

                writer.WriteString(name, normalized);
            }
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxAttributesBytes)
            return (null, "attributes_too_large");

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var hash = Sha256Hex(buffer.WrittenSpan);
        return (new ParsedAttributes(json, hash, sortedKeys.Count == 0), null);
    }

    private static bool TryNormalize(AttributeDef def, string raw, out string normalized)
    {
        normalized = "";
        switch (def.Type)
        {
            case "string":
                var trimmed = raw.Trim();
                if (trimmed.Length == 0) return false;
                if (def.MaxLength is { } cap && trimmed.Length > cap) return false;
                normalized = trimmed;
                return true;

            case "email":
                var email = raw.Trim().ToLowerInvariant();
                if (email.Length == 0) return false;
                if (def.MaxLength is { } emailCap && email.Length > emailCap) return false;
                if (!IsBasicEmail(email)) return false;
                normalized = email;
                return true;

            case "e164":
                var phone = raw.Trim();
                if (def.MaxLength is { } phoneCap && phone.Length > phoneCap) return false;
                if (!IsE164(phone)) return false;
                normalized = phone;
                return true;

            case "enum":
                if (def.Values is null) return false;
                foreach (var allowed in def.Values)
                {
                    if (raw == allowed) { normalized = raw; return true; }
                }
                return false;

            default:
                return false;
        }
    }

    private static bool IsBasicEmail(string s)
    {
        var at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        if (s.IndexOf('@', at + 1) >= 0) return false;
        if (s.IndexOf('.', at + 1) < 0) return false;
        foreach (var c in s) if (char.IsWhiteSpace(c)) return false;
        return true;
    }

    private static bool IsE164(string s)
    {
        if (s.Length < 9 || s.Length > 16 || s[0] != '+') return false;
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    private static bool TryUuid(JsonElement el, string key, out Guid? value)
    {
        value = null;
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind == JsonValueKind.Null) return true;
        if (prop.ValueKind != JsonValueKind.String || !Guid.TryParse(prop.GetString(), out var parsed))
            return false;
        value = parsed;
        return true;
    }
}
