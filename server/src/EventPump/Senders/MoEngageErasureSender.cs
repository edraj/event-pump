using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Data;
using EventPump.Worker;

namespace EventPump.Senders;

public sealed class MoEngageErasureSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly HttpClient _http;
    private readonly int _senderTimeoutMs;

    public MoEngageErasureSender(
        TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        _senderTimeoutMs = senderTimeoutMs;
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
        if (item.EventName == TrackingPlan.AttributesErasureRequestedEventName)
            return await ClearAttributesAsync(customerId, ct);

        // The person's profile is not the only one MoEngage may hold for them:
        // a device the Flutter SDK keyed on its anonymous_id before login is a
        // profile of its own (EventStore.ResolveMoEngageDevicesAsync). The
        // delete API takes one user per call, so each device gets its own.
        //
        // Devices go first and the person last. A device MoEngage has no
        // profile for answers 400 "Not Found" — it never sent an event, or a
        // previous pass already deleted it — and that is the state we want,
        // so it counts as deleted. The person's own delete keeps its strict
        // mapping: a 400 there may mean we are naming the wrong customer, and
        // `delivered` would hide it. Putting the person last means a pass cut
        // short by a retry never re-sends the person's delete after it
        // already succeeded, which would come back 400 and settle `dead`.
        var devices = EventStore.ErasureHandles.FromContextJson(item.ContextJson).MoEngageDevices
            .Where(device => !string.Equals(device, customerId, StringComparison.Ordinal))
            .ToArray();
        var deleted = 0;
        SendResult? failure = null;
        foreach (var device in devices)
        {
            // Same reason as AdjustErasureSender: the lease covers the whole
            // fan-out, and a second worker must not re-claim it mid-way.
            if (OutOfLease(item))
            {
                failure = SendResult.Retry("lease_expiring");
                break;
            }

            var result = await DeleteAsync(device, deviceProfile: true, ct);
            if (result.Outcome == SendOutcome.Delivered)
            {
                deleted++;
                continue;
            }
            if (result.Outcome == SendOutcome.Retry) { failure = result; break; }
            failure ??= result;
        }

        if (failure?.Outcome != SendOutcome.Retry)
        {
            var person = OutOfLease(item) && devices.Length > 0
                ? SendResult.Retry("lease_expiring")
                : await DeleteAsync(customerId, deviceProfile: false, ct);
            // A retry outranks a device's permanent rejection, as in
            // AdjustErasureSender: re-driving is the only way the person's
            // delete gets another attempt.
            if (person.Outcome == SendOutcome.Retry) failure = person;
            else if (person.Outcome != SendOutcome.Delivered) failure ??= person;
        }

        if (failure is not { } outcome) return SendResult.Delivered();
        // A lone person profile keeps the bare result it always had; with
        // devices in play the count makes a partial erasure legible in the
        // audit trail.
        if (devices.Length == 0) return outcome;
        var detail = $"{outcome.Detail} ({deleted}/{devices.Length} device profiles deleted)";
        return outcome.Outcome == SendOutcome.Retry
            ? SendResult.Retry(detail)
            : SendResult.Dead(detail);
    }

    private bool OutOfLease(DeliveryItem item) =>
        item.LeaseExpiresAt is { } expiresAt
        && DateTime.UtcNow.AddMilliseconds(_senderTimeoutMs) >= expiresAt;

    private async Task<SendResult> DeleteAsync(string customerId, bool deviceProfile, CancellationToken ct)
    {
        var url = $"{_tenant.MoEngageEndpoint}/v1/customer/delete"
                  + $"?app_id={Uri.EscapeDataString(_tenant.MoEngageAppId)}";
        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("identity_type", "customer_id");
            writer.WriteString("identity_value", customerId);
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();
            var status = (int)response.StatusCode;
            if (deviceProfile && status == 400
                && IsUserNotFound(await response.Content.ReadAsStringAsync(ct)))
                return SendResult.Delivered();
            return ErasureHttp.Map(status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    // MoEngage's delete API answers an unknown customer with
    // 400 {"status":"fail","error":{"type":"Not Found", ...}} (docs verified
    // 2026-10). Any other 400 is a rejected request and stays one.
    private static bool IsUserNotFound(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var error)
                   && SenderUtil.GetString(error, "type") == "Not Found";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<SendResult> ClearAttributesAsync(string customerId, CancellationToken ct)
    {
        var names = _tenant.Plan.Attributes.Keys
            .Select(name => name == "phone" ? "mobile" : name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0) return SendResult.Skip("no_attributes");
        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "customer");
            writer.WriteString("customer_id", customerId);
            writer.WriteStartObject("attributes");
            foreach (var name in names) writer.WriteNull(name);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                $"{_tenant.MoEngageEndpoint}/v1/customer/{Uri.EscapeDataString(_tenant.MoEngageAppId)}",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
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
