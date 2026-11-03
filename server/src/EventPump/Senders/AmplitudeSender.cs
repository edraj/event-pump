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
/// session_id = the Amplitude SDK's own session when it was forwarded, else
/// session start ms (recovered from the UUIDv7 session_key) — see
/// <see cref="SessionId"/>.
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
        using var eventContextDocument = JsonDocument.Parse(item.ContextJson);
        var eventContext = eventContextDocument.RootElement;
        // EP's own taxonomy, stamped at ingestion (EventValidation.DerivePlatform).
        // emit_event stores no platform, and everything it writes is server-side.
        var sourcePlatform = SenderUtil.GetString(eventContext, "platform")
                             ?? (item.Origin == "server" ? "backend" : "unknown");

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
            if (SessionId(item, eventContext) is { } sessionId)
                writer.WriteNumber("session_id", sessionId);
            var os = SenderUtil.GetString(context, "os");
            if (os is not null) writer.WriteString("os_name", os);
            if (AmplitudePlatform(sourcePlatform, os) is { } platform)
                writer.WriteString("platform", platform);
            if (SenderUtil.GetString(context, "os_version") is { } osVersion) writer.WriteString("os_version", osVersion);
            if (SenderUtil.GetString(context, "brand") is { } brand) writer.WriteString("device_brand", brand);
            if (SenderUtil.GetString(context, "model") is { } model) writer.WriteString("device_model", model);
            if (SenderUtil.GetString(context, "language") is { } language) writer.WriteString("language", language);
            if (SenderUtil.GetString(context, "app_version") is { } appVersion) writer.WriteString("app_version", appVersion);
            if (identity?.ClientIp is { } ip) writer.WriteString("ip", ip);
            writer.WritePropertyName("event_properties");
            writer.WriteStartObject();
            // A producer may hand emit_event a jsonb null or an array, and the
            // plan's resolver passes those through; Amplitude only takes an
            // object, so they contribute nothing rather than throwing on every
            // attempt and counting against the circuit breaker.
            if (properties.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in properties.RootElement.EnumerateObject())
                {
                    // EP owns this one: a producer's copy, or a plan rename
                    // onto it, would put the key in the object twice.
                    if (prop.NameEquals(SourcePlatformProperty)) continue;
                    prop.WriteTo(writer);
                }
            }
            writer.WriteString(SourcePlatformProperty, sourcePlatform);
            writer.WriteEndObject();
            if (attributes is not null) WriteUserProperties(writer, attributes.RootElement);
            writer.WriteEndObject();
            writer.WriteEndArray();
            // Our user_ids are not guaranteed to satisfy Amplitude's default
            // 5-char minimum; without this the id value is silently dropped.
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

    private const string SourcePlatformProperty = "source_platform";

    // How far a session's start may sit after the event and still be the one
    // the event belongs to: the Amplitude session id comes from the device's
    // clock and a server event's time from ours.
    private static readonly long ClockSkewMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    /// <summary>
    /// The Amplitude session an event belongs to, best evidence first:
    ///
    /// 1. The id the Flutter SDK stamped on the event's own context when it
    ///    was tracked — the session the app was actually in. A client event
    ///    without one was tracked before the app forwarded any, and is not
    ///    guessed onto one: the session row may hold the previous launch's
    ///    id, or a later session's.
    /// 2. For a server event, which nothing stamps, the id last forwarded for
    ///    its session row (identify's `amplitude_session_id`). The row is read
    ///    at delivery, not when the event happened, so it may already name a
    ///    later session; an Amplitude session id is its start in epoch ms,
    ///    and an event cannot belong to a session that started after it, so
    ///    such a value is passed over. A person-resolved row never gets here:
    ///    the worker blanks its session fields.
    /// 3. EP's own session start, recovered from the UUIDv7 session_key.
    /// 4. -1 for a server event outside any session, which keeps it out of
    ///    session-based metrics.
    /// </summary>
    private static long? SessionId(DeliveryItem item, JsonElement eventContext)
    {
        if (SenderUtil.TryParseAmplitudeSessionId(eventContext, "amplitude_session_id", out var tracked))
            return tracked;
        var occurredMs = new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        if (item.Origin == "server"
            && SenderUtil.TryParseAmplitudeSessionId(item.Identity?.AmplitudeSessionId, out var forwarded)
            && forwarded <= occurredMs + ClockSkewMs)
            return forwarded;
        if (SenderUtil.SessionStartMs(item.SessionKey) is { } sessionStart) return sessionStart;
        return item.Origin == "server" ? -1 : null;
    }

    /// <summary>
    /// Amplitude's own `platform` column, in the values its SDKs write:
    /// `Web` for an event that happened on the web — a mobile browser's
    /// session still reads Android, and that is web traffic — else `Android` /
    /// `iOS` from the session's os, else omitted. EP's own app / web / backend
    /// tag travels separately, as `source_platform`.
    /// </summary>
    private static string? AmplitudePlatform(string sourcePlatform, string? os)
    {
        if (sourcePlatform == "web") return "Web";
        if (os is null) return null;
        if (os.Contains("android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (os.Contains("ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
        return null;
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
