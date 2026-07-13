using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// GA4 Measurement Protocol sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint}/mp/collect?api_secret=..&amp;measurement_id=.. (web streams,
/// body client_id) or ?firebase_app_id=.. (app streams, body app_instance_id).
/// device{}, user_location{}, ip_override and user_agent are top-level payload
/// fields (added to MP in 2025). The live endpoint never returns validation
/// errors — any 2xx only means "received".
/// </summary>
public sealed class Ga4Sender : IDestinationSender
{
    private const int MaxParams = 25;

    private readonly EpConfig _config;
    private readonly HttpClient _http;

    public Ga4Sender(EpConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _http = SenderUtil.CreateClient(config, handler);
    }

    public string Destination => "ga4";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var identity = item.Identity;
        string query;
        string idField;
        string idValue;
        if (identity?.Ga4ClientId is { } clientId && _config.Ga4MeasurementId is { } measurementId)
        {
            query = $"measurement_id={Uri.EscapeDataString(measurementId)}";
            (idField, idValue) = ("client_id", clientId);
        }
        else if (identity?.FirebaseAppInstanceId is { } appInstanceId && _config.Ga4FirebaseAppId is { } firebaseAppId)
        {
            query = $"firebase_app_id={Uri.EscapeDataString(firebaseAppId)}";
            (idField, idValue) = ("app_instance_id", appInstanceId);
        }
        else
        {
            return SendResult.Skip("no_ga4_identity"); // never fabricate identity
        }

        var url = $"{_config.Ga4Endpoint}/mp/collect?{query}&api_secret={Uri.EscapeDataString(_config.Ga4ApiSecret)}";

        using var properties = JsonDocument.Parse(item.PropertiesJson);
        using var eventContext = JsonDocument.Parse(item.ContextJson);
        using var registryContext = JsonDocument.Parse(identity!.ContextJson);

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(idField, idValue);
            var userId = item.UserId ?? identity.UserId;
            if (userId is not null) writer.WriteString("user_id", userId);
            writer.WriteNumber("timestamp_micros",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000);
            if (identity.ClientIp is not null) writer.WriteString("ip_override", identity.ClientIp);

            var context = registryContext.RootElement;
            if (SenderUtil.GetString(context, "user_agent") is { } userAgent)
                writer.WriteString("user_agent", userAgent);
            WriteDevice(writer, context);

            writer.WriteStartArray("events");
            writer.WriteStartObject();
            writer.WriteString("name", item.EventName);
            writer.WriteStartObject("params");
            var paramCount = 0;
            if (identity.Ga4SessionId is { } sessionId)
            {
                writer.WriteString("session_id", sessionId);
                paramCount++;
            }
            if (eventContext.RootElement.ValueKind == JsonValueKind.Object
                && eventContext.RootElement.TryGetProperty("engagement_time_msec", out var engagement)
                && engagement.ValueKind == JsonValueKind.Number)
            {
                writer.WritePropertyName("engagement_time_msec");
                engagement.WriteTo(writer);
                paramCount++;
            }
            if (properties.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.RootElement.EnumerateObject())
                {
                    if (paramCount >= MaxParams) break;
                    // MP params are scalar-only
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    property.WriteTo(writer);
                    paramCount++;
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();
            var status = (int)response.StatusCode;
            return status == 429 || status >= 500
                ? SendResult.Retry($"http_{status}")
                : SendResult.Dead($"http_{status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    private static void WriteDevice(Utf8JsonWriter writer, JsonElement context)
    {
        // registry context key -> GA4 device{} field (SPEC §5 table)
        (string From, string To)[] map =
        [
            ("category", "category"),
            ("language", "language"),
            ("screen_resolution", "screen_resolution"),
            ("os", "operating_system"),
            ("os_version", "operating_system_version"),
            ("model", "model"),
        ];
        var any = map.Any(m => SenderUtil.GetString(context, m.From) is not null);
        if (!any) return;
        writer.WriteStartObject("device");
        foreach (var (from, to) in map)
        {
            if (SenderUtil.GetString(context, from) is { } value) writer.WriteString(to, value);
        }
        writer.WriteEndObject();
    }
}
