using System.Net.Http.Headers;
using System.Text;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

public sealed class MoEngageErasureSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly HttpClient _http;

    public MoEngageErasureSender(
        TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{tenant.MoEngageAppId}:{tenant.MoEngageApiKey}")));
    }

    public string AppId => _tenant.AppId;
    public string Destination => TrackingPlan.MoEngageErasureDestination;

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        if (!_tenant.MoEngageErasureEnabled) return SendResult.Skip("erasure_disabled");
        if (item.UserId is not { } userId) return SendResult.Skip("no_user_id");

        var customerId = ErasureHttp.Handle(item.ContextJson, "moengage_customer_id", userId);
        var attributesOnly = item.EventName == TrackingPlan.AttributesErasureRequestedEventName;

        var url = attributesOnly
            ? $"{_tenant.MoEngageEndpoint}/v1/customer/{Uri.EscapeDataString(_tenant.MoEngageAppId)}"
            : $"{_tenant.MoEngageEndpoint}/v1/customer/delete"
              + $"?app_id={Uri.EscapeDataString(_tenant.MoEngageAppId)}";

        string payload;
        if (attributesOnly)
        {
            var names = _tenant.Plan.Attributes.Keys
                .Select(name => name == "phone" ? "mobile" : name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (names.Length == 0) return SendResult.Skip("no_attributes");
            payload = SenderUtil.WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "customer");
                writer.WriteString("customer_id", customerId);
                writer.WriteStartObject("attributes");
                foreach (var name in names) writer.WriteNull(name);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        else
        {
            payload = SenderUtil.WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("identity_type", "customer_id");
                writer.WriteString("identity_value", customerId);
                writer.WriteEndObject();
            });
        }

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            return response.IsSuccessStatusCode
                ? SendResult.Delivered()
                : ErasureHttp.Map((int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }
}
