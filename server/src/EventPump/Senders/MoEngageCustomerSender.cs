using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>
/// MoEngage type:"customer" sender (SPEC §6.1) — sets user profile attributes
/// on MoEngage as a distinct destination from the event sender. Enqueued as
/// the reserved `ep_attributes_synced` event by the /v1/identity handler when
/// user_attributes.hash diverges from moengage_synced_hash.
///
/// Race-safety (SPEC §6.1): the sender captures `attributes` AND `hash` in
/// one SELECT (keyed on (app_id, user_id)), builds the payload from those
/// captured attributes, sends, and on success writes back
/// `moengage_synced_hash = &lt;captured hash&gt;` — the hash of the payload
/// actually sent. Concurrent setUserAttributes lands within the same tenant
/// only, so the row's `hash` is guaranteed to move ahead of the captured value
/// if it changed mid-flight.
/// </summary>
public sealed class MoEngageCustomerSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly NpgsqlDataSource _dataSource;
    private readonly HttpClient _http;

    public MoEngageCustomerSender(TenantConfig tenant, int senderTimeoutMs,
        NpgsqlDataSource dataSource, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _dataSource = dataSource;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{tenant.MoEngageAppId}:{tenant.MoEngageApiKey}")));
    }

    public string AppId => _tenant.AppId;
    public string Destination => "moengage_customer";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        // SPEC §12: the flag gates delivery, not registration — rows enqueued
        // while it was on still need a terminal state after it is turned off.
        if (!_tenant.MoEngageAttributesEnabled) return SendResult.Skip("attributes_disabled");
        if (item.UserId is not { } userId) return SendResult.Skip("no_user_id");

        // Race-safe fetch: capture attributes AND hash together, then send only
        // what was captured — never re-read at write-back time.
        string capturedJson;
        string? capturedHash;
        await using (var fetch = _dataSource.CreateCommand(
            "SELECT attributes::text, hash FROM user_attributes WHERE app_id = $1 AND user_id = $2"))
        {
            fetch.Parameters.Add(new() { Value = _tenant.AppId });
            fetch.Parameters.Add(new() { Value = userId });
            await using var reader = await fetch.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return SendResult.Skip("no_attributes");
            capturedJson = reader.GetString(0);
            capturedHash = await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1);
        }

        if (capturedHash is null) return SendResult.Skip("no_attributes");
        using var parsed = JsonDocument.Parse(capturedJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object
            || !HasAnyProperty(parsed.RootElement))
            return SendResult.Skip("no_attributes");

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "customer");
            writer.WriteString("customer_id", userId);
            writer.WritePropertyName("attributes");
            WriteMoEngageAttributes(writer, parsed.RootElement);
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                $"{_tenant.MoEngageEndpoint}/v1/customer/{Uri.EscapeDataString(_tenant.MoEngageAppId)}",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return status == 429 || status >= 500
                    ? SendResult.Retry($"http_{status}")
                    : SendResult.Dead($"http_{status}");
            }

            // Write back the hash of the payload we actually sent — never the row's
            // current hash at this moment (SPEC §6.1 race-safety clause).
            await using var writeBack = _dataSource.CreateCommand(
                "UPDATE user_attributes SET moengage_synced_hash = $1, moengage_synced_at = now() WHERE app_id = $2 AND user_id = $3");
            writeBack.Parameters.Add(new() { Value = capturedHash });
            writeBack.Parameters.Add(new() { Value = _tenant.AppId });
            writeBack.Parameters.Add(new() { Value = userId });
            await writeBack.ExecuteNonQueryAsync(ct);

            return SendResult.Delivered();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps our canonical attribute names to MoEngage's expected keys
    /// (SPEC §6.1 mapping table): phone → mobile; everything else pass-through.
    /// Values are already normalized at ingestion (email lowercased, phone
    /// E.164, gender canonical enum) — MoEngage accepts them as-is.
    /// </summary>
    private static void WriteMoEngageAttributes(Utf8JsonWriter writer, JsonElement attributes)
    {
        writer.WriteStartObject();
        foreach (var property in attributes.EnumerateObject())
        {
            var key = property.Name == "phone" ? "mobile" : property.Name;
            writer.WritePropertyName(key);
            property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool HasAnyProperty(JsonElement obj)
    {
        foreach (var _ in obj.EnumerateObject()) return true;
        return false;
    }
}
