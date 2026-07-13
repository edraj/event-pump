using System.Text.Json;
using static EventPump.Data.EventStore;

namespace EventPump.Api;

/// <summary>Parses /v1/identity bodies (SPEC §9.2). Strict envelope, lenient handles.</summary>
public static class IdentityValidation
{
    private static readonly HashSet<string> TopLevelKeys =
        ["session_key", "anonymous_id", "session_number", "user_id", "first_seen_at", "handles", "context"];

    public static (IdentityUpsert? Identity, string? Error) Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, "not_an_object");

        foreach (var property in root.EnumerateObject())
        {
            if (!TopLevelKeys.Contains(property.Name)) return (null, $"unknown_field:{property.Name}");
        }

        if (!TryUuid(root, "session_key", out var sessionKey) || sessionKey is null)
            return (null, "invalid_session_key");
        if (!TryUuid(root, "anonymous_id", out var anonymousId) || anonymousId is null)
            return (null, "invalid_anonymous_id");

        int? sessionNumber = null;
        if (root.TryGetProperty("session_number", out var num) && num.ValueKind != JsonValueKind.Null)
        {
            if (num.ValueKind != JsonValueKind.Number || !num.TryGetInt32(out var parsed) || parsed < 1)
                return (null, "invalid_session_number");
            sessionNumber = parsed;
        }

        string? userId = null;
        if (root.TryGetProperty("user_id", out var user) && user.ValueKind != JsonValueKind.Null)
        {
            if (user.ValueKind != JsonValueKind.String || user.GetString() is not { Length: > 0 and <= 256 } uid)
                return (null, "invalid_user_id");
            userId = uid;
        }

        string? clickIdsJson = null;
        string? ga4ClientId = null, ga4SessionId = null, firebaseAppInstanceId = null;
        string? amplitudeDeviceId = null, adjustAdid = null, adjustPlatformAdId = null, fbp = null, fbc = null;
        if (root.TryGetProperty("handles", out var handles) && handles.ValueKind != JsonValueKind.Null)
        {
            if (handles.ValueKind != JsonValueKind.Object) return (null, "invalid_handles");
            foreach (var handle in handles.EnumerateObject())
            {
                if (handle.Name == "click_ids")
                {
                    if (handle.Value.ValueKind != JsonValueKind.Object) return (null, "invalid_click_ids");
                    clickIdsJson = handle.Value.GetRawText();
                    continue;
                }
                if (handle.Value.ValueKind == JsonValueKind.Null) continue;
                if (handle.Value.ValueKind != JsonValueKind.String
                    || handle.Value.GetString() is not { Length: > 0 and <= 512 } value)
                    return (null, $"invalid_handle:{handle.Name}");
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
            if (context.ValueKind != JsonValueKind.Object) return (null, "invalid_context");
            contextJson = context.GetRawText();
        }

        return (new IdentityUpsert(
            sessionKey.Value, anonymousId.Value, sessionNumber, userId,
            ga4ClientId, ga4SessionId, firebaseAppInstanceId, amplitudeDeviceId,
            adjustAdid, adjustPlatformAdId, fbp, fbc, clickIdsJson, contextJson), null);
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
