using System.Net.Http.Headers;
using System.Text;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

public sealed class AmplitudeErasureSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly HttpClient _http;

    public AmplitudeErasureSender(
        TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        if (!string.IsNullOrEmpty(tenant.AmplitudeSecretKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{tenant.AmplitudeApiKey}:{tenant.AmplitudeSecretKey}")));
        }
    }

    public string AppId => _tenant.AppId;
    public string Destination => TrackingPlan.AmplitudeErasureDestination;

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        if (!_tenant.AmplitudeErasureEnabled) return SendResult.Skip("erasure_disabled");

        // The deletion API authenticates with the API key AND the secret key,
        // which the event sender never needs. Skipping loudly beats sending an
        // unauthenticated delete and recording the 401 as a dead erasure.
        if (string.IsNullOrEmpty(_tenant.AmplitudeSecretKey))
            return SendResult.Skip("no_secret_key");

        var userId = ErasureHttp.HandleOrNull(item.ContextJson, "amplitude_user_id")
                     ?? item.UserId;
        var deviceId = ErasureHttp.HandleOrNull(item.ContextJson, "amplitude_device_id");
        if (userId is null && deviceId is null) return SendResult.Skip("no_amplitude_identity");

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            if (userId is not null)
            {
                writer.WriteStartArray("user_ids");
                writer.WriteStringValue(userId);
                writer.WriteEndArray();
            }
            if (deviceId is not null)
            {
                writer.WriteStartArray("device_ids");
                writer.WriteStringValue(deviceId);
                writer.WriteEndArray();
            }
            writer.WriteString("requester", "eventpump");
            // Deliberately false. `ignore_invalid_id: true` makes Amplitude
            // answer 2xx for ids it holds nothing under, which we would record
            // as `delivered` — a DSR reported complete against a profile that
            // was never touched. That matters most on the `item.UserId`
            // fallback above: our user ids are not guaranteed to satisfy
            // Amplitude's default 5-character minimum (AmplitudeSender sends
            // events under `min_id_length: 1`, which the deletion API has no
            // equivalent for), so an id Amplitude will not match is the likely
            // case, not the exotic one. False turns that into a 4xx, which
            // ErasureHttp.Map records `dead` — visible in the audit trail and
            // re-drivable, rather than a silent false success.
            writer.WriteBoolean("ignore_invalid_id", false);
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                _tenant.AmplitudeErasureEndpoint,
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
