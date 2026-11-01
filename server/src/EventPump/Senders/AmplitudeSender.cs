using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Data;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>
/// Amplitude HTTP API V2 sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint} with api_key in the JSON body; insert_id = event_id gives a
/// 7-day dedupe window that makes retries safe; time in epoch milliseconds;
/// session_id = session start ms (recovered from the UUIDv7 session_key).
///
/// User attributes (SPEC §6.1) are emitted as inline `user_properties` on the
/// event object (all six allowlisted keys pass through by name) when the
/// tenant enables Amplitude attributes and the user has a user_attributes row.
/// </summary>
public sealed class AmplitudeSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly TrackingPlan _plan;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly HttpClient _http;

    public AmplitudeSender(TenantConfig tenant, int senderTimeoutMs,
        NpgsqlDataSource? dataSource = null, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _plan = tenant.Plan;
        _dataSource = dataSource;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
    }

    public string AppId => _tenant.AppId;
    public string Destination => "amplitude";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var identity = item.Identity;
        var deviceId = identity?.AmplitudeDeviceId;
        var wireUserId = SenderUtil.WireUserId(item.UserId, identity?.UserId, identity?.AmplitudeUserId);
        var backendWithNoSessionToWaitFor =
            item.Origin == "server" && item.SessionKey is null && wireUserId is not null;
        if (deviceId is null && !backendWithNoSessionToWaitFor)
            return SenderUtil.MissingIdentity(item, "no_amplitude_device_id");

        // SPEC §6.2 R3: rename property keys before writing event_properties.
        using var properties = JsonDocument.Parse(
            _plan.ResolvePropertiesJson(item.EventName, "amplitude", item.PropertiesJson));
        using var registryContext = JsonDocument.Parse(identity?.ContextJson ?? "{}");
        var context = registryContext.RootElement;
        // EP stamps `platform` on the event's own context at ingestion
        // (EventValidation.DerivePlatform): app / web / backend / unknown.
        // That taxonomy is authoritative; identity.os is only the device OS of
        // whatever session the event resolved against, which for a web session
        // on an Android device is `Android` and would otherwise merge web
        // traffic into the native Android column — see finding #36 / #37.
        using var eventContext = JsonDocument.Parse(item.ContextJson);
        var eventPlatform = SenderUtil.GetString(eventContext.RootElement, "platform");
        var isWeb = eventPlatform == "web";
        // source_platform is EP's taxonomy, so prefer the event's own
        // `context.platform` when present (web clearly; the SDK stamps
        // app / web). Fall back to item.Origin (`server` ⇒ backend) for
        // producers that don't set it, and `app` as the ultimate default.
        var sourcePlatform = eventPlatform switch
        {
            "web" => "web",
            "app" => "app",
            "backend" => "backend",
            "unknown" => "unknown",
            _ => item.Origin == "server" ? "backend" : "app",
        };

        var effectiveUserId = item.UserId ?? identity?.UserId;
        var attributesJson = _tenant.AmplitudeAttributesEnabled && _dataSource is not null && effectiveUserId is not null
            ? await EventStore.FetchUserAttributesJsonAsync(_dataSource, _tenant.AppId, effectiveUserId, ct)
            : null;
        using var attributes = attributesJson is null ? null : JsonDocument.Parse(attributesJson);

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("api_key", _tenant.AmplitudeApiKey);
            writer.WriteStartArray("events");
            writer.WriteStartObject();
            writer.WriteString("event_type", _plan.ResolveEventName(item.EventName, "amplitude"));
            writer.WriteString("insert_id", item.EventId.ToString());
            if (deviceId is not null) writer.WriteString("device_id", deviceId);
            if (wireUserId is not null) writer.WriteString("user_id", wireUserId);
            writer.WriteNumber("time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
            // Amplitude session_id resolution order:
            //   1. the event's own context.amplitude_session_id — stamped by
            //      the Flutter SDK at track-time, so an event that happened
            //      in session S keeps S even if it is flushed after a
            //      rotation or retried hours later;
            //   2. the identity row's AmplitudeSessionId — the live value for
            //      backend events during an active app session, and the only
            //      value an older SDK (one that doesn't stamp per-event) can
            //      offer;
            //   3. the UUIDv7-derived start_ms when no Amplitude session is
            //      known yet (early mobile event before setHandles landed);
            //   4. `-1` for server events outside any live app session
            //      (TSD slide 12 — excluded from session-based metrics).
            if (long.TryParse(
                    SenderUtil.GetString(eventContext.RootElement, "amplitude_session_id"),
                    out var ampSessionId))
                writer.WriteNumber("session_id", ampSessionId);
            else if (long.TryParse(identity?.AmplitudeSessionId, out ampSessionId))
                writer.WriteNumber("session_id", ampSessionId);
            else if (SenderUtil.SessionStartMs(item.SessionKey) is { } sessionStart)
                writer.WriteNumber("session_id", sessionStart);
            else if (item.Origin == "server")
                writer.WriteNumber("session_id", -1);
            var os = SenderUtil.GetString(context, "os");
            if (os is not null) writer.WriteString("os_name", os);
            // TSD slide 15 field contract: `platform` is Amplitude's native
            // iOS/Android column — EP's own app/web/backend taxonomy rides on
            // `source_platform` in event_properties below so native Amplitude
            // charts keep working. For web events, emit `web` so a Chrome-on-
            // Android session doesn't bleed into the native Android bucket.
            if (isWeb) writer.WriteString("platform", "web");
            else if (os is not null) writer.WriteString("platform", os);
            if (SenderUtil.GetString(context, "os_version") is { } osVersion) writer.WriteString("os_version", osVersion);
            if (SenderUtil.GetString(context, "brand") is { } brand) writer.WriteString("device_brand", brand);
            if (SenderUtil.GetString(context, "model") is { } model) writer.WriteString("device_model", model);
            if (SenderUtil.GetString(context, "language") is { } language) writer.WriteString("language", language);
            if (SenderUtil.GetString(context, "app_version") is { } appVersion) writer.WriteString("app_version", appVersion);
            if (identity?.ClientIp is { } ip) writer.WriteString("ip", ip);
            writer.WritePropertyName("event_properties");
            writer.WriteStartObject();
            // source_platform is EP's authoritative taxonomy (from the event's
            // context.platform set at ingestion), so it is written first and
            // the plan enumeration below skips any clashing key. Previously
            // the stamp came after the plan, which produced a duplicate JSON
            // key whenever a producer or per-destination rename emitted
            // source_platform too.
            writer.WriteString("source_platform", sourcePlatform);
            // ResolvePropertiesJson tolerates non-objects (jsonb null, arrays)
            // and emit_event only coalesces to '{}'. EnumerateObject() throws
            // InvalidOperationException on anything but an object, which the
            // worker maps to Retry — one such producer in a loop opens the
            // tenant's Amplitude circuit breaker and pauses the whole feed.
            if (properties.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in properties.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("source_platform")) continue;
                    prop.WriteTo(writer);
                }
            writer.WriteEndObject();
            if (attributes is not null) WriteUserProperties(writer, attributes.RootElement);
            writer.WriteEndObject();
            writer.WriteEndArray();
            // Amplitude silently drops user_id / device_id values shorter than
            // 5 characters. Our ids (plain numeric customer ids, Guid session
            // keys, etc.) are not guaranteed to clear that bar, so lower it
            // for this ingestion rather than rewriting the ids — the
            // documented one-knob fix. Payload option, per-request; no
            // tenant-specific prefix in shared code, no mobile SDK change,
            // and the erasure sender stays in step by not padding either.
            writer.WriteStartObject("options");
            writer.WriteNumber("min_id_length", 1);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                _tenant.AmplitudeEndpoint, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();
            var status = (int)response.StatusCode;
            // Amplitude's HTTP V2 API does not answer a bad key with 401 — it
            // answers `400 {"code":400,"error":"Invalid API key: ..."}`. Left
            // to the shared mapper that lands in the Dead arm alongside genuine
            // payload rejections, which is the one case here worth recovering,
            // so it is read off the body rather than the status line.
            if (status == 400)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (body.Contains("invalid api key", StringComparison.OrdinalIgnoreCase))
                    return SendResult.AuthFailed("http_400_invalid_api_key");
            }
            return status switch
            {
                429 => SendResult.Retry("http_429_throttled"),
                // insert_id makes retry duplicate-safe
                _ => SenderUtil.MapStatus(status),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    /// <summary>
    /// SPEC §6.1 Amplitude mapping: every allowlisted attribute passes through
    /// as an inline `user_property` on each event (Amplitude accepts them raw).
    /// The allowlist comes from the tracking plan — the declared source of truth
    /// per §6.1 — so adding an attribute there reaches Amplitude without an
    /// edit here. Destinations with a per-name mapping (GA4, Adjust) still need
    /// their own tables; a pass-through does not.
    /// </summary>
    private void WriteUserProperties(Utf8JsonWriter writer, JsonElement attributes)
    {
        var any = false;
        foreach (var property in attributes.EnumerateObject())
        {
            if (!_plan.Attributes.ContainsKey(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (!any) { writer.WriteStartObject("user_properties"); any = true; }
            writer.WritePropertyName(property.Name);
            property.Value.WriteTo(writer);
        }
        if (any) writer.WriteEndObject();
    }
}
