using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// Amplitude HTTP API V2 sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint} with api_key in the JSON body; insert_id = event_id gives a
/// 7-day dedupe window that makes retries safe; time in epoch milliseconds;
/// session_id = session start ms (recovered from the UUIDv7 session_key).
/// </summary>
public sealed class AmplitudeSender : IDestinationSender
{
    private readonly EpConfig _config;
    private readonly HttpClient _http;

    public AmplitudeSender(EpConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _http = SenderUtil.CreateClient(config, handler);
    }

    public string Destination => "amplitude";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var identity = item.Identity;
        if (identity?.AmplitudeDeviceId is not { } deviceId)
            return SendResult.Skip("no_amplitude_device_id"); // never mint a separate id

        using var properties = JsonDocument.Parse(item.PropertiesJson);
        using var registryContext = JsonDocument.Parse(identity.ContextJson);
        var context = registryContext.RootElement;

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("api_key", _config.AmplitudeApiKey);
            writer.WriteStartArray("events");
            writer.WriteStartObject();
            writer.WriteString("event_type", item.EventName);
            writer.WriteString("insert_id", item.EventId.ToString());
            writer.WriteString("device_id", deviceId);
            if ((item.UserId ?? identity.UserId) is { } userId) writer.WriteString("user_id", userId);
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
                _config.AmplitudeEndpoint, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
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
}
