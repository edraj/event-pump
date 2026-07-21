using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Model;

namespace EventPump.Api;

/// <summary>A validated event ready for storage. JSON payloads stay as raw strings.</summary>
public sealed record ParsedEvent(
    Guid EventId,
    string EventName,
    DateTimeOffset OccurredAt,
    Guid? AnonymousId,
    Guid? SessionKey,
    string? UserId,
    string PropertiesJson,
    string ContextJson);

/// <summary>Server-side event validation (SPEC §1), per event, independently.</summary>
public static class EventValidation
{
    public const int MaxEventBytes = 32 * 1024;
    public const int MaxBatchSize = 100;

    private static readonly HashSet<string> TopLevelKeys =
    [
        "event_id", "event_name", "occurred_at", "anonymous_id",
        "session_key", "user_id", "properties", "context",
    ];

    // SPEC §5: per-event context carries only these; unknown keys silently dropped.
    private static readonly HashSet<string> ContextKeys =
        ["page", "screen", "engagement_time_msec", "session_number", "sdk"];

    public static (List<ParsedEvent> Valid, List<RejectedEvent> Rejected) ValidateBatch(
        JsonElement events, string origin, TrackingPlan plan, string? clientIp, DateTimeOffset now)
    {
        var valid = new List<ParsedEvent>();
        var rejected = new List<RejectedEvent>();
        var seenIds = new HashSet<Guid>();
        var index = -1;
        foreach (var element in events.EnumerateArray())
        {
            index++;
            var (parsed, reason) = ValidateOne(element, origin, plan, clientIp, now);
            if (parsed is null)
            {
                rejected.Add(new RejectedEvent(index, TryGetEventId(element), reason!));
            }
            else if (seenIds.Add(parsed.EventId))
            {
                valid.Add(parsed); // in-batch duplicates: keep first, still "accepted"
            }
        }
        return (valid, rejected);
    }

    private static (ParsedEvent?, string?) ValidateOne(
        JsonElement el, string origin, TrackingPlan plan, string? clientIp, DateTimeOffset now)
    {
        if (el.ValueKind != JsonValueKind.Object) return (null, "not_an_object");
        if (Encoding.UTF8.GetByteCount(el.GetRawText()) > MaxEventBytes) return (null, "event_too_large");

        foreach (var property in el.EnumerateObject())
        {
            if (!TopLevelKeys.Contains(property.Name)) return (null, $"unknown_field:{property.Name}");
        }

        if (!TryGetUuid(el, "event_id", out var eventId) || eventId is null)
            return (null, "invalid_event_id");

        if (!el.TryGetProperty("event_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
            || nameEl.GetString() is not { } name || !EventName.IsValid(name))
            return (null, "invalid_event_name");
        if (!plan.Events.TryGetValue(name, out var planEvent) || planEvent.Origin != origin)
            return (null, "unknown_event_name");
        if (planEvent.Reserved)
            return (null, "reserved_event_name");

        if (!el.TryGetProperty("occurred_at", out var occEl) || occEl.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(occEl.GetString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out var occurredAt))
            return (null, "invalid_occurred_at");
        if (occurredAt <= now.AddDays(-7) || occurredAt >= now.AddHours(1))
            return (null, "occurred_at_out_of_range");

        if (!TryGetUuid(el, "anonymous_id", out var anonymousId))
            return (null, "invalid_anonymous_id");
        if (origin == "client" && anonymousId is null)
            return (null, "missing_anonymous_id");

        if (!TryGetUuid(el, "session_key", out var sessionKey))
            return (null, "invalid_session_key");

        string? userId = null;
        if (el.TryGetProperty("user_id", out var userEl) && userEl.ValueKind != JsonValueKind.Null)
        {
            if (userEl.ValueKind != JsonValueKind.String || userEl.GetString() is not { Length: > 0 and <= 256 } uid)
                return (null, "invalid_user_id");
            userId = uid;
        }

        var propertiesJson = "{}";
        if (el.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind != JsonValueKind.Null)
        {
            if (propsEl.ValueKind != JsonValueKind.Object) return (null, "invalid_properties");
            propertiesJson = propsEl.GetRawText();
        }

        JsonElement? contextEl = null;
        if (el.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind != JsonValueKind.Null)
        {
            if (ctxEl.ValueKind != JsonValueKind.Object) return (null, "invalid_context");
            contextEl = ctxEl;
        }

        return (new ParsedEvent(eventId.Value, name, occurredAt, anonymousId, sessionKey, userId,
            propertiesJson, FilterContext(contextEl, clientIp)), null);
    }

    /// <summary>Keeps only SPEC §5 per-event keys and injects the server-observed ip.</summary>
    private static string FilterContext(JsonElement? context, string? clientIp)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (context is { } ctx)
            {
                foreach (var property in ctx.EnumerateObject())
                {
                    if (ContextKeys.Contains(property.Name)) property.WriteTo(writer);
                }
            }
            if (clientIp is not null) writer.WriteString("ip", clientIp);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryGetUuid(JsonElement el, string key, out Guid? value)
    {
        value = null;
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind == JsonValueKind.Null) return true;
        if (prop.ValueKind != JsonValueKind.String || !Guid.TryParse(prop.GetString(), out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static string? TryGetEventId(JsonElement el)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty("event_id", out var id)
           && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
