using System.Net.Http.Headers;
using System.Text;
using EventPump.Config;
using EventPump.Data;
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

        var handles = EventStore.ErasureHandles.FromContextJson(item.ContextJson);
        var userId = handles.AmplitudeUserId ?? item.UserId;
        // Every device the person was seen on, not just the newest: the
        // deletion API takes `device_ids` as an array, and an older device left
        // out keeps its pre-login events while this row reads `delivered`.
        var deviceIds = handles.AmplitudeDevices;
        if (userId is null && deviceIds.Count == 0)
            return SendResult.Skip("no_amplitude_identity");

        // `amplitude_device_id` is the browser's anonymous_id, so a person who
        // clears cookies or uses many browsers accumulates one per device and
        // they all arrive here. Sent as one array, a large enough set is a
        // payload Amplitude answers 4xx to — and ErasureHttp.Map records a 4xx
        // `dead` on the first attempt, abandoning the erasure for good. Chunked
        // instead, because a request nobody can retry is the failure mode this
        // sender is least allowed to have. The person delete rides the first
        // chunk; repeating it on every chunk would ask Amplitude to delete the
        // same profile n times.
        var chunks = Chunk(deviceIds, MaxDeviceIdsPerRequest);
        var sent = 0;
        SendResult? failure = null;
        foreach (var chunk in chunks)
        {
            var result = await DeleteAsync(sent == 0 ? userId : null, chunk, ct);
            if (result.Outcome == SendOutcome.Delivered) { sent++; continue; }
            // Ends the pass either way: a 4xx here is the payload shape or the
            // credentials, which the next chunk shares, and a retry means
            // Amplitude is unreachable. Both are answered by the whole
            // delivery being re-driven, and the deletion API is idempotent.
            failure = result;
            break;
        }

        if (failure is not { } outcome) return SendResult.Delivered();
        var detail = chunks.Count > 1
            ? $"{outcome.Detail} ({sent}/{chunks.Count} batches)"
            : outcome.Detail!;
        return outcome.Outcome == SendOutcome.Retry
            ? SendResult.Retry(detail)
            : SendResult.Dead(detail);
    }

    // Amplitude documents no hard ceiling on `device_ids`, so this is a size
    // we know is safe rather than the largest that works.
    private const int MaxDeviceIdsPerRequest = 100;

    private static List<IReadOnlyList<string>> Chunk(IReadOnlyList<string> ids, int size)
    {
        // One chunk even when there are no device ids at all: the request
        // still has to go out to delete the person by user id.
        var chunks = new List<IReadOnlyList<string>>();
        for (var start = 0; start < ids.Count; start += size)
            chunks.Add([.. ids.Skip(start).Take(size)]);
        if (chunks.Count == 0) chunks.Add([]);
        return chunks;
    }

    private async Task<SendResult> DeleteAsync(
        string? userId, IReadOnlyList<string> deviceIds, CancellationToken ct)
    {
        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            if (userId is not null)
            {
                writer.WriteStartArray("user_ids");
                writer.WriteStringValue(userId);
                writer.WriteEndArray();
            }
            if (deviceIds.Count > 0)
            {
                writer.WriteStartArray("device_ids");
                foreach (var deviceId in deviceIds) writer.WriteStringValue(deviceId);
                writer.WriteEndArray();
            }
            writer.WriteString("requester", "eventpump");
            // Deliberately false. `ignore_invalid_id: true` makes Amplitude
            // answer 2xx for ids it holds nothing under, which we would record
            // as `delivered` — a DSR reported complete against a profile that
            // was never touched. That matters most when the caller's own
            // user id is the handle: our user ids are not guaranteed to satisfy
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
