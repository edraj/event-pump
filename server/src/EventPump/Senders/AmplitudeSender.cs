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
        if (identity?.AmplitudeDeviceId is not { } deviceId)
            return SendResult.Skip("no_amplitude_device_id"); // never mint a separate id

        // SPEC §6.2 R3: rename property keys before writing event_properties.
        using var properties = JsonDocument.Parse(
            _plan.ResolvePropertiesJson(item.EventName, "amplitude", item.PropertiesJson));
        using var registryContext = JsonDocument.Parse(identity.ContextJson);
        var context = registryContext.RootElement;

        var effectiveUserId = item.UserId ?? identity.UserId;
        // Per-destination user_id: prefer identity's Amplitude-specific handle
        // when the app set one via identify(), else fall back to the generic
        // user_id. Attribute lookup stays on the generic id.
        var wireUserId = identity.AmplitudeUserId ?? effectiveUserId;
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
            writer.WriteString("device_id", deviceId);
            if (wireUserId is not null) writer.WriteString("user_id", wireUserId);
            writer.WriteNumber("time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
            if (SenderUtil.SessionStartMs(item.SessionKey) is { } sessionStart)
                writer.WriteNumber("session_id", sessionStart);
            if (SenderUtil.GetString(context, "os") is { } os) writer.WriteString("os_name", os);
            if (SenderUtil.GetString(context, "os_version") is { } osVersion) writer.WriteString("os_version", osVersion);
            if (SenderUtil.GetString(context, "model") is { } model) writer.WriteString("device_model", model);
            if (SenderUtil.GetString(context, "language") is { } language) writer.WriteString("language", language);
            if (SenderUtil.GetString(context, "app_version") is { } appVersion) writer.WriteString("app_version", appVersion);
            if (identity.ClientIp is { } ip) writer.WriteString("ip", ip);
            writer.WritePropertyName("event_properties");
            properties.RootElement.WriteTo(writer);
            if (attributes is not null) WriteUserProperties(writer, attributes.RootElement);
            writer.WriteEndObject();
            writer.WriteEndArray();
            // our user_ids are not guaranteed to satisfy Amplitude's default
            // 5-char minimum; without this the id value is silently dropped
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
            return status switch
            {
                429 => SendResult.Retry("http_429_throttled"),
                >= 500 => SendResult.Retry($"http_{status}"), // insert_id makes retry duplicate-safe
                _ => SendResult.Dead($"http_{status}"),
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
