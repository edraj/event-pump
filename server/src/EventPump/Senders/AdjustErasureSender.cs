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
        // recorded device there is nothing to name, and reporting `delivered`
        // would claim an erasure that never happened.
        //
        // Which parameter carries the device is not interchangeable, and this
        // mirrors AdjustSender exactly: `adid` is Adjust's own device id,
        // while adjust_platform_ad_id is the raw platform advertising id —
        // IDFA on iOS, GAID on Android — which Adjust only recognises under
        // `idfa` / `gps_adid`. Sending a GAID as `adid` matches no device, and
        // Adjust answers 200 either way, so the row would read `delivered`
        // while the device was never forgotten.
        var device = Device(item.ContextJson);
        if (device is not { } parameter) return SendResult.Skip("no_adjust_device");

        var url = $"{_tenant.AdjustErasureEndpoint}?app_token="
                  + $"{Uri.EscapeDataString(_tenant.AdjustAppToken)}"
                  + $"&{parameter.Name}={Uri.EscapeDataString(parameter.Value)}";

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

    // Same resolution order as AdjustSender: the Adjust device id when we have
    // one, else the platform advertising id under the parameter its os names.
    // An os we cannot classify leaves the id unusable rather than guessed.
    private static (string Name, string Value)? Device(string contextJson)
    {
        if (ErasureHttp.HandleOrNull(contextJson, "adjust_adid") is { } adid)
            return ("adid", adid);
        if (ErasureHttp.HandleOrNull(contextJson, "adjust_platform_ad_id") is not { } platformAdId)
            return null;
        return ErasureHttp.HandleOrNull(contextJson, "os") switch
        {
            { } os when os.Contains("android", StringComparison.OrdinalIgnoreCase)
                => ("gps_adid", platformAdId),
            { } os when os.Contains("ios", StringComparison.OrdinalIgnoreCase)
                => ("idfa", platformAdId),
            _ => null,
        };
    }
}
