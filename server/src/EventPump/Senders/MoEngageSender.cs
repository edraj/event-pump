using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// MoEngage Data API sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint}/v1/event/{appId}, HTTP Basic auth (workspaceId:dataApiKey),
/// body {type:"event", customer_id, actions:[{action, attributes,
/// current_time (epoch seconds), platform}]}. customer_id maps to OUR
/// user_id — absent user_id => skipped. Receives `first_visit` (SPEC §8).
/// </summary>
public sealed class MoEngageSender : IDestinationSender
{
    private readonly EpConfig _config;
    private readonly TrackingPlan _plan;
    private readonly HttpClient _http;

    public MoEngageSender(EpConfig config, TrackingPlan plan, HttpMessageHandler? handler = null)
    {
        _config = config;
        _plan = plan;
        _http = SenderUtil.CreateClient(config, handler);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.MoEngageAppId}:{config.MoEngageApiKey}")));
    }

    public string Destination => "moengage";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        if ((item.UserId ?? item.Identity?.UserId) is not { } customerId)
            return SendResult.Skip("no_user_id");

        // SPEC §6.2 R3: rename property keys before writing attributes.
        using var properties = JsonDocument.Parse(
            _plan.ResolvePropertiesJson(item.EventName, "moengage", item.PropertiesJson));
        var platform = "web";
        if (item.Identity is { } identity)
        {
            using var registryContext = JsonDocument.Parse(identity.ContextJson);
            var os = SenderUtil.GetString(registryContext.RootElement, "os") ?? "";
            if (os.Contains("android", StringComparison.OrdinalIgnoreCase)) platform = "ANDROID";
            else if (os.Contains("ios", StringComparison.OrdinalIgnoreCase)) platform = "iOS";
        }

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "event");
            writer.WriteString("customer_id", customerId);
            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", _plan.ResolveEventName(item.EventName, "moengage"));
            writer.WritePropertyName("attributes");
            properties.RootElement.WriteTo(writer);
            writer.WriteString("platform", platform);
            writer.WriteNumber("current_time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeSeconds());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                $"{_config.MoEngageEndpoint}/v1/event/{Uri.EscapeDataString(_config.MoEngageAppId)}",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
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
}
