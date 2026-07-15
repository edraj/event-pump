using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Data;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>
/// GA4 Measurement Protocol sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint}/mp/collect?api_secret=..&amp;measurement_id=.. (web streams,
/// body client_id) or ?firebase_app_id=.. (app streams, body app_instance_id).
/// device{}, user_location{}, ip_override and user_agent are top-level payload
/// fields (added to MP in 2025). The live endpoint never returns validation
/// errors — any 2xx only means "received".
///
/// User attributes (SPEC §6.1) are emitted as top-level `user_properties` (raw
/// descriptors: first_name, last_name, gender, city) plus `user_data`
/// (SHA-256 hashed identifiers: email, phone) when EP_GA4_ATTRIBUTES_ENABLED
/// is on and the user has a user_attributes row.
/// </summary>
public sealed class Ga4Sender : IDestinationSender
{
    private const int MaxParams = 25;

    private readonly EpConfig _config;
    private readonly NpgsqlDataSource? _dataSource;
    private readonly HttpClient _http;

    public Ga4Sender(EpConfig config, NpgsqlDataSource? dataSource = null, HttpMessageHandler? handler = null)
    {
        _config = config;
        _dataSource = dataSource;
        _http = SenderUtil.CreateClient(config, handler);
    }

    public string Destination => "ga4";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var identity = item.Identity;
        string query;
        string idField;
        string idValue;
        if (identity?.Ga4ClientId is { } clientId && _config.Ga4MeasurementId is { } measurementId)
        {
            query = $"measurement_id={Uri.EscapeDataString(measurementId)}";
            (idField, idValue) = ("client_id", clientId);
        }
        else if (identity?.FirebaseAppInstanceId is { } appInstanceId && _config.Ga4FirebaseAppId is { } firebaseAppId)
        {
            query = $"firebase_app_id={Uri.EscapeDataString(firebaseAppId)}";
            (idField, idValue) = ("app_instance_id", appInstanceId);
        }
        else
        {
            return SendResult.Skip("no_ga4_identity"); // never fabricate identity
        }

        var url = $"{_config.Ga4Endpoint}/mp/collect?{query}&api_secret={Uri.EscapeDataString(_config.Ga4ApiSecret)}";

        using var properties = JsonDocument.Parse(item.PropertiesJson);
        using var eventContext = JsonDocument.Parse(item.ContextJson);
        using var registryContext = JsonDocument.Parse(identity!.ContextJson);

        var effectiveUserId = item.UserId ?? identity.UserId;
        var attributesJson = _config.Ga4AttributesEnabled && _dataSource is not null && effectiveUserId is not null
            ? await EventStore.FetchUserAttributesJsonAsync(_dataSource, effectiveUserId, ct)
            : null;
        using var attributes = attributesJson is null ? null : JsonDocument.Parse(attributesJson);

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(idField, idValue);
            if (effectiveUserId is not null) writer.WriteString("user_id", effectiveUserId);
            writer.WriteNumber("timestamp_micros",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000);
            if (identity.ClientIp is not null) writer.WriteString("ip_override", identity.ClientIp);

            var context = registryContext.RootElement;
            if (SenderUtil.GetString(context, "user_agent") is { } userAgent)
                writer.WriteString("user_agent", userAgent);
            WriteDevice(writer, context);
            if (attributes is not null) WriteAttributes(writer, attributes.RootElement);

            writer.WriteStartArray("events");
            writer.WriteStartObject();
            writer.WriteString("name", item.EventName);
            writer.WriteStartObject("params");
            var paramCount = 0;
            if (identity.Ga4SessionId is { } sessionId)
            {
                writer.WriteString("session_id", sessionId);
                paramCount++;
            }
            if (eventContext.RootElement.ValueKind == JsonValueKind.Object
                && eventContext.RootElement.TryGetProperty("engagement_time_msec", out var engagement)
                && engagement.ValueKind == JsonValueKind.Number)
            {
                writer.WritePropertyName("engagement_time_msec");
                engagement.WriteTo(writer);
                paramCount++;
            }
            if (properties.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.RootElement.EnumerateObject())
                {
                    if (paramCount >= MaxParams) break;
                    // MP params are scalar-only
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    property.WriteTo(writer);
                    paramCount++;
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
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

    /// <summary>
    /// SPEC §6.1 GA4 mapping: first_name/last_name/gender/city → user_properties
    /// (as `{name: {value: str}}` per GA4 MP schema); email/phone →
    /// user_data.sha256_email_address / sha256_phone_number (phone SHA-256 taken
    /// over E.164 with the leading `+` stripped per Google's guidance).
    /// </summary>
    private static void WriteAttributes(Utf8JsonWriter writer, JsonElement attributes)
    {
        string? emailHash = null, phoneHash = null;
        var hasDescriptor = false;
        foreach (var property in attributes.EnumerateObject())
        {
            switch (property.Name)
            {
                case "email" when property.Value.ValueKind == JsonValueKind.String:
                    emailHash = SenderUtil.Sha256Hex(property.Value.GetString()!);
                    break;
                case "phone" when property.Value.ValueKind == JsonValueKind.String:
                    phoneHash = SenderUtil.Sha256Hex(property.Value.GetString()!.TrimStart('+'));
                    break;
                case "first_name" or "last_name" or "gender" or "city":
                    hasDescriptor |= property.Value.ValueKind == JsonValueKind.String;
                    break;
            }
        }

        if (hasDescriptor)
        {
            writer.WriteStartObject("user_properties");
            foreach (var property in attributes.EnumerateObject())
            {
                if (property.Name is not ("first_name" or "last_name" or "gender" or "city")) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                writer.WriteStartObject(property.Name);
                writer.WriteString("value", property.Value.GetString());
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        if (emailHash is not null || phoneHash is not null)
        {
            writer.WriteStartObject("user_data");
            if (emailHash is not null) writer.WriteString("sha256_email_address", emailHash);
            if (phoneHash is not null) writer.WriteString("sha256_phone_number", phoneHash);
            writer.WriteEndObject();
        }
    }

    private static void WriteDevice(Utf8JsonWriter writer, JsonElement context)
    {
        // registry context key -> GA4 device{} field (SPEC §5 table)
        (string From, string To)[] map =
        [
            ("category", "category"),
            ("language", "language"),
            ("screen_resolution", "screen_resolution"),
            ("os", "operating_system"),
            ("os_version", "operating_system_version"),
            ("model", "model"),
        ];
        var any = map.Any(m => SenderUtil.GetString(context, m.From) is not null);
        if (!any) return;
        writer.WriteStartObject("device");
        foreach (var (from, to) in map)
        {
            if (SenderUtil.GetString(context, from) is { } value) writer.WriteString(to, value);
        }
        writer.WriteEndObject();
    }
}
