using System.Text;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

public sealed class AdjustErasureSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly HttpClient _http;

    public AdjustErasureSender(
        TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
    }

    public string AppId => _tenant.AppId;
    public string Destination => TrackingPlan.AdjustErasureDestination;

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        if (!_tenant.AdjustErasureEnabled) return SendResult.Skip("erasure_disabled");

        // Adjust erases a device, not a person: it has no concept of our
        // user_id, so there is no fallback the way MoEngage has one. Without a
        // recorded adid there is nothing to name, and reporting `delivered`
        // would claim an erasure that never happened.
        var adid = ErasureHttp.HandleOrNull(item.ContextJson, "adjust_adid")
                   ?? ErasureHttp.HandleOrNull(item.ContextJson, "adjust_platform_ad_id");
        if (adid is null) return SendResult.Skip("no_adjust_device");

        var url = $"{_tenant.AdjustErasureEndpoint}?app_token="
                  + $"{Uri.EscapeDataString(_tenant.AdjustAppToken)}"
                  + $"&adid={Uri.EscapeDataString(adid)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (_tenant.AdjustS2sToken is { } token)
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Content = new StringContent("", Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct);
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
