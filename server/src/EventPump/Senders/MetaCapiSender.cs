using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// Meta Conversions API reference sender (SPEC §12) — the one shipped
/// PixelPlatformSender subclass, disabled by default. Docs verified 2026-07:
/// POST {endpoint}/{graphVersion}/{pixelId}/events?access_token=..;
/// data[{event_name (translated via the plan's meta_name), event_time
/// (seconds, max 7 days old), event_id (48h dedupe vs the browser pixel),
/// action_source, user_data (hashed em/ph, external_id, fbp/fbc, ip, UA),
/// custom_data}]; top-level test_event_code when configured (testing only).
/// </summary>
public sealed class MetaCapiSender : PixelPlatformSender
{
    private static readonly int[] RetryableErrorCodes = [1, 2, 4, 17, 341];

    private readonly EpConfig _config;
    private readonly TrackingPlan _plan;
    private readonly HttpClient _http;

    public MetaCapiSender(EpConfig config, TrackingPlan plan, HttpMessageHandler? handler = null)
        : base("meta", config.MetaConsentGating)
    {
        _config = config;
        _plan = plan;
        _http = SenderUtil.CreateClient(config, handler);
    }

    protected override async Task<SendResult> SendCoreAsync(
        DeliveryItem item, PixelUserData userData, CancellationToken ct)
    {
        if (userData is { EmailSha256: null, PhoneSha256: null, ExternalId: null,
                          Fbp: null, Fbc: null, ClientIp: null, UserAgent: null })
            return SendResult.Skip("no_user_data");

        var eventName = _plan.Events.TryGetValue(item.EventName, out var planEvent)
            ? planEvent.MetaName ?? item.EventName
            : item.EventName;

        var url = $"{_config.MetaEndpoint}/{_config.MetaGraphVersion}/{_config.MetaPixelId}/events" +
                  $"?access_token={Uri.EscapeDataString(_config.MetaAccessToken)}";

        using var properties = JsonDocument.Parse(item.PropertiesJson);
        var props = properties.RootElement;

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("data");
            writer.WriteStartObject();
            writer.WriteString("event_name", eventName);
            writer.WriteNumber("event_time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeSeconds());
            writer.WriteString("event_id", item.EventId.ToString());
            writer.WriteString("action_source", _config.MetaActionSource);

            writer.WriteStartObject("user_data");
            if (userData.EmailSha256 is { } em) writer.WriteString("em", em);
            if (userData.PhoneSha256 is { } ph) writer.WriteString("ph", ph);
            if (userData.ExternalId is { } externalId)
                writer.WriteString("external_id", Sha256Lower(externalId)); // hashing recommended by docs
            if (userData.Fbp is { } fbp) writer.WriteString("fbp", fbp);
            if (userData.Fbc is { } fbc) writer.WriteString("fbc", fbc);
            if (userData.ClientIp is { } ip) writer.WriteString("client_ip_address", ip);
            if (userData.UserAgent is { } userAgent) writer.WriteString("client_user_agent", userAgent);
            writer.WriteEndObject();

            if (props.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject("custom_data");
                if (props.TryGetProperty("revenue", out var revenue) && revenue.ValueKind == JsonValueKind.Number)
                {
                    writer.WritePropertyName("value");
                    revenue.WriteTo(writer);
                }
                if (props.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String)
                    writer.WriteString("currency", currency.GetString());
                if (props.TryGetProperty("order_id", out var orderId) && orderId.ValueKind == JsonValueKind.String)
                    writer.WriteString("order_id", orderId.GetString());
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            if (_config.MetaTestEventCode is { } testCode)
                writer.WriteString("test_event_code", testCode);
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();

            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            var errorCode = ParseErrorCode(body);
            if (status >= 500 || (errorCode is { } code && RetryableErrorCodes.Contains(code)))
                return SendResult.Retry($"http_{status}_code_{errorCode}");
            return SendResult.Dead($"http_{status}_code_{errorCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    private static int? ParseErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
