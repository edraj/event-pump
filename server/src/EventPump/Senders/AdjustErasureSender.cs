using System.Text;
using EventPump.Config;
using EventPump.Data;
using EventPump.Worker;

namespace EventPump.Senders;

public sealed class AdjustErasureSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly HttpClient _http;
    private readonly int _senderTimeoutMs;

    public AdjustErasureSender(
        TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        _senderTimeoutMs = senderTimeoutMs;
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
        // gdpr_forget_device takes one device per call, so every device the
        // person was seen on gets its own request: erasing only the newest
        // leaves their older phone tracked while this row reads `delivered`.
        var recorded = EventStore.ErasureHandles.FromContextJson(item.ContextJson).Adjust;
        var sendable = new List<(string Name, string Value)>();
        var unclassifiable = 0;
        foreach (var device in recorded)
        {
            if (Parameter(device) is { } parameter) sendable.Add(parameter);
            // An ad id whose os we cannot classify is a device we hold and
            // cannot erase. Counted, never dropped: skipping it quietly would
            // let a person with one usable and one unclassifiable device come
            // back `delivered` with the second still tracked.
            else unclassifiable++;
        }
        if (sendable.Count == 0) return SendResult.Skip("no_adjust_device");

        var forgotten = 0;
        SendResult? failure = null;
        foreach (var (name, value) in sendable)
        {
            // The lease covers the whole delivery, not one call, and this is
            // the only sender that makes N of them. Stopping while the lease
            // still holds keeps a second worker from re-claiming the row
            // mid-fan-out and running the same calls alongside us.
            if (OutOfLease(item))
            {
                failure = SendResult.Retry("lease_expiring");
                break;
            }

            var result = await ForgetAsync(name, value, ct);
            if (result.Outcome == SendOutcome.Delivered)
            {
                forgotten++;
                continue;
            }
            // A retry outranks a permanent rejection: re-driving the delivery
            // is the only way a device that failed transiently gets another
            // attempt, and forget_device is idempotent, so the devices already
            // forgotten cost nothing on the second pass. It also ends the
            // pass — Adjust being unreachable or throttling us is not a fact
            // about this device, and walking the rest would spend one sender
            // timeout apiece proving it.
            if (result.Outcome == SendOutcome.Retry) { failure = result; break; }
            failure ??= result;
        }

        // Every count is over the devices we hold, not the ones we could
        // address, so a partly completed erasure is legible in the audit trail
        // rather than reading like a single failed call. A lone device keeps
        // the bare reason it always had.
        var detail = failure?.Detail ?? "no_adjust_device";
        if (recorded.Count > 1) detail += $" ({forgotten}/{recorded.Count} forgotten)";

        if (failure is { } outcome)
            return outcome.Outcome == SendOutcome.Retry
                ? SendResult.Retry(detail)
                : SendResult.Dead(detail);

        // Everything we could address is forgotten. That is not the same as
        // the person being forgotten, and only `dead` says so in a way the
        // audit trail keeps and a later request can re-drive.
        return unclassifiable == 0 ? SendResult.Delivered() : SendResult.Dead(detail);
    }

    // Mirrors the worker's own pre-send check (DeliveryWorker.ConsumeAsync):
    // no lease recorded means nothing to outrun.
    private bool OutOfLease(DeliveryItem item) =>
        item.LeaseExpiresAt is { } expiresAt
        && DateTime.UtcNow.AddMilliseconds(_senderTimeoutMs) >= expiresAt;

    private async Task<SendResult> ForgetAsync(string name, string value, CancellationToken ct)
    {
        // `s2s=1` marks this a server-to-server call, the same way AdjustSender
        // marks the event endpoint. Without it Adjust can reject the request,
        // and ErasureHttp.Map records any non-429/non-5xx as `dead` — one
        // rejected attempt and the erasure is abandoned for good.
        var url = $"{_tenant.AdjustErasureEndpoint}?s2s=1&app_token="
                  + $"{Uri.EscapeDataString(_tenant.AdjustAppToken)}"
                  + $"&{name}={Uri.EscapeDataString(value)}";

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

    // Which parameter carries the device is not interchangeable, and this
    // mirrors AdjustSender exactly: `adid` is Adjust's own device id, while
    // adjust_platform_ad_id is the raw platform advertising id — IDFA on iOS,
    // GAID on Android — which Adjust only recognises under `idfa` / `gps_adid`.
    // Sending a GAID as `adid` matches no device, and Adjust answers 200 either
    // way, so the row would read `delivered` while the device was never
    // forgotten. An os we cannot classify leaves the id unusable rather than
    // guessed; the os is the one recorded alongside this device's ad id, not
    // whichever the person's newest session happened to carry.
    private static (string Name, string Value)? Parameter(EventStore.AdjustDevice device)
    {
        if (device.Adid is { } adid) return ("adid", adid);
        if (device.PlatformAdId is not { } platformAdId) return null;
        return device.Os switch
        {
            { } os when os.Contains("android", StringComparison.OrdinalIgnoreCase)
                => ("gps_adid", platformAdId),
            { } os when os.Contains("ios", StringComparison.OrdinalIgnoreCase)
                => ("idfa", platformAdId),
            _ => null,
        };
    }
}
