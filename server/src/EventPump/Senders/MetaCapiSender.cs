using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>
/// Meta Conversions API reference sender (SPEC §12) — the one shipped
/// PixelPlatformSender subclass, disabled by default. Docs verified 2026-07:
/// POST {endpoint}/{graphVersion}/{pixelId}/events?access_token=..;
/// data[{event_name (SPEC §6.2 rename via destinations.meta.events.&lt;x&gt;.name),
/// event_time (seconds, max 7 days old), event_id (48h dedupe vs the browser
/// pixel), action_source, user_data (hashed em/ph, external_id, fbp/fbc, ip,
/// UA), custom_data}]; top-level test_event_code when configured (testing only).
/// </summary>
public sealed class MetaCapiSender : PixelPlatformSender
{
    private static readonly int[] RetryableErrorCodes = [1, 2, 4, 17, 341];

    /// <summary>
    /// Graph API error codes that mean "your credentials are the problem",
    /// which Meta reports in the body with a 400 rather than on the status
    /// line — so a status-only mapping never sees them.
    ///
    ///   190 OAuthException — access token expired, revoked or invalid
    ///   102 session key invalid / expired
    ///   10  application does not have permission for this action
    ///   200 permission error (missing scope on the token)
    ///
    /// This matters more here than on any other destination: Meta system-user
    /// tokens actually expire, where MoEngage/Amplitude/Adjust credentials are
    /// static tenant-file values. Before this, a token expiring at 09:00 made
    /// every CAPI event from 09:00 onwards `dead` with `http_400_code_190`,
    /// which is terminal and unrecoverable.
    /// </summary>
    private static readonly int[] AuthErrorCodes = [10, 102, 190, 200];

    private readonly TenantConfig _tenant;
    private readonly TrackingPlan _plan;
    private readonly HttpClient _http;

    public MetaCapiSender(
        TenantConfig tenant,
        int senderTimeoutMs,
        NpgsqlDataSource? dataSource = null,
        HttpMessageHandler? handler = null)
        : base(tenant.AppId, "meta", tenant.MetaConsentGating, dataSource, tenant.MetaAttributesEnabled)
    {
        _tenant = tenant;
        _plan = tenant.Plan;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
    }

    protected override async Task<SendResult> SendCoreAsync(
        DeliveryItem item, PixelUserData userData, CancellationToken ct)
    {
        if (userData is { EmailSha256: null, PhoneSha256: null, ExternalId: null,
                          Fbp: null, Fbc: null, ClientIp: null, UserAgent: null })
            return SenderUtil.MissingIdentity(item, "no_user_data");

        // SPEC §6.2 R1/R2: destinations.meta.events.<x>.name wins, else canonical.
        var eventName = _plan.ResolveEventName(item.EventName, "meta");

        var url = $"{_tenant.MetaEndpoint}/{_tenant.MetaGraphVersion}/{_tenant.MetaPixelId}/events" +
                  $"?access_token={Uri.EscapeDataString(_tenant.MetaAccessToken)}";

        using var properties = JsonDocument.Parse(item.PropertiesJson);
        var props = properties.RootElement;

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("data");
            writer.WriteStartObject();
            writer.WriteString("event_name", eventName);
            writer.WriteNumber("event_time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeSeconds());
            writer.WriteString("event_id", item.EventId.ToString());
            writer.WriteString("action_source", ActionSource(item, _tenant.MetaActionSource));

            writer.WriteStartObject("user_data");
            if (userData.EmailSha256 is { } em) writer.WriteString("em", em);
            if (userData.PhoneSha256 is { } ph) writer.WriteString("ph", ph);
            if (userData.ExternalId is { } externalId)
                writer.WriteString("external_id", Sha256Lower(externalId)); // hashing recommended by docs
            if (userData.Fbp is { } fbp) writer.WriteString("fbp", fbp);
            if (userData.Fbc is { } fbc) writer.WriteString("fbc", fbc);
            if (userData.ClientIp is { } ip) writer.WriteString("client_ip_address", ip);
            if (userData.UserAgent is { } userAgent) writer.WriteString("client_user_agent", userAgent);
            writer.WriteEndObject();

            if (props.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject("custom_data");
                if (props.TryGetProperty("revenue", out var revenue) && revenue.ValueKind == JsonValueKind.Number)
                {
                    writer.WritePropertyName("value");
                    revenue.WriteTo(writer);
                }
                if (props.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String)
                    writer.WriteString("currency", currency.GetString());
                if (props.TryGetProperty("order_id", out var orderId) && orderId.ValueKind == JsonValueKind.String)
                    writer.WriteString("order_id", orderId.GetString());
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
            if (_tenant.MetaTestEventCode is { } testCode)
                writer.WriteString("test_event_code", testCode);
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();

            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct);
            var errorCode = ParseErrorCode(body);
            // Checked before the retryable codes and before the status arm: the
            // code is the specific signal and the 400 carrying it is the vague
            // one, so the code decides whenever Meta supplied it.
            if (errorCode is { } authCode && AuthErrorCodes.Contains(authCode))
                return SendResult.AuthFailed($"http_{status}_code_{errorCode}");
            if (status >= 500 || (errorCode is { } code && RetryableErrorCodes.Contains(code)))
                return SendResult.Retry($"http_{status}_code_{errorCode}");
            // No error code to go on: fall back to the shared status mapping so
            // a bare 401/403 is still read as a credential failure.
            if (errorCode is null && status is 401 or 403)
                return SendResult.AuthFailed($"http_{status}");
            return SendResult.Dead($"http_{status}_code_{errorCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    private static string ActionSource(DeliveryItem item, string configured)
    {
        using var context = JsonDocument.Parse(item.ContextJson);
        return SenderUtil.GetString(context.RootElement, "platform") switch
        {
            "web" => "website",
            "app" => "app",
            "backend" => "system_generated",
            _ => configured,
        };
    }

    private static int? ParseErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
