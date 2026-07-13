using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// Adjust S2S events sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint} form-encoded (NOT JSON): s2s=1, app_token, event_token
/// (from the tracking plan), a device identifier (adid preferred, else the
/// platform ad id mapped by OS), created_at_unix (max 58 days old), revenue in
/// full currency units + currency, ip_address (IPv4 only — their AEM/callback
/// requirement), user_agent; Bearer S2S security token when configured.
/// No protocol-level idempotency: our outbox dedupe is the guarantee.
/// </summary>
public sealed class AdjustSender : IDestinationSender
{
    private readonly EpConfig _config;
    private readonly TrackingPlan _plan;
    private readonly HttpClient _http;

    public AdjustSender(EpConfig config, TrackingPlan plan, HttpMessageHandler? handler = null)
    {
        _config = config;
        _plan = plan;
        _http = SenderUtil.CreateClient(config, handler);
        if (config.AdjustS2sToken is { } token)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string Destination => "adjust";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var eventToken = _plan.Events.TryGetValue(item.EventName, out var planEvent)
            ? planEvent.AdjustToken
            : null;
        if (eventToken is null) return SendResult.Skip("no_event_token");

        var identity = item.Identity;
        var form = new List<KeyValuePair<string, string>>
        {
            new("s2s", "1"),
            new("app_token", _config.AdjustAppToken),
            new("event_token", eventToken),
        };

        string? os = null;
        if (identity is not null)
        {
            using var registryContext = JsonDocument.Parse(identity.ContextJson);
            os = SenderUtil.GetString(registryContext.RootElement, "os");
            if (SenderUtil.GetString(registryContext.RootElement, "user_agent") is { } userAgent)
                form.Add(new("user_agent", userAgent));
        }

        if (identity?.AdjustAdid is { } adid)
        {
            form.Add(new("adid", adid));
        }
        else if (identity?.AdjustPlatformAdId is { } platformAdId && os is not null)
        {
            var param = os.Contains("android", StringComparison.OrdinalIgnoreCase) ? "gps_adid"
                : os.Contains("ios", StringComparison.OrdinalIgnoreCase) ? "idfa"
                : null;
            if (param is null) return SendResult.Skip("no_adjust_adid");
            form.Add(new(param, platformAdId));
        }
        else
        {
            return SendResult.Skip("no_adjust_adid"); // web never sets adid — intended (SPEC §6)
        }

        form.Add(new("created_at_unix",
            new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture)));

        using (var properties = JsonDocument.Parse(item.PropertiesJson))
        {
            var root = properties.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("revenue", out var revenue)
                && revenue.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("currency", out var currency)
                && currency.ValueKind == JsonValueKind.String)
            {
                form.Add(new("revenue", revenue.GetDouble().ToString(CultureInfo.InvariantCulture)));
                form.Add(new("currency", currency.GetString()!));
            }
        }

        // IPv4 only per Adjust docs; IPv6 would be rejected
        if (identity?.ClientIp is { } ip && !ip.Contains(':'))
            form.Add(new("ip_address", ip));

        var body = string.Join('&',
            form.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        try
        {
            using var response = await _http.PostAsync(_config.AdjustEndpoint,
                new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct);
            var status = (int)response.StatusCode;
            if (status == 202)
                return SendResult.Dead("s2s_auth_misconfigured"); // accepted transport, discarded data
            if (response.IsSuccessStatusCode) return SendResult.Delivered();
            if (status == 404 || status >= 500) return SendResult.Retry($"http_{status}");
            if (status == 400)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (text.Contains("earlier unique event tracked", StringComparison.OrdinalIgnoreCase))
                    return SendResult.Skip("duplicate_unique_event");
            }
            return SendResult.Dead($"http_{status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }
}
