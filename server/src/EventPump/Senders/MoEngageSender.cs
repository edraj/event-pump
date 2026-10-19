using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// MoEngage Data API sender (SPEC §12). Docs verified 2026-07:
/// POST {endpoint}/v1/event/{appId}, HTTP Basic auth (workspaceId:dataApiKey),
/// body {type:"event", customer_id, actions:[{action, attributes,
/// current_time (epoch seconds), platform}]}. customer_id is the session's
/// `moengage_customer_id` handle when identify() set one, else OUR user_id;
/// a handle that is the device's own anonymous_id (the Flutter SDK's opt-in
/// pre-login mode) is used only while nobody is logged in — see
/// <see cref="CustomerId"/>. Nothing to key on => skipped `no_user_id`.
/// `platform` is MoEngage's fixed enum, never EP's own tag — see
/// <see cref="ResolvePlatform"/>. Receives `first_visit` (SPEC §8).
/// </summary>
public sealed class MoEngageSender : IDestinationSender
{
    private readonly TenantConfig _tenant;
    private readonly TrackingPlan _plan;
    private readonly HttpClient _http;

    public MoEngageSender(TenantConfig tenant, int senderTimeoutMs, HttpMessageHandler? handler = null)
    {
        _tenant = tenant;
        _plan = tenant.Plan;
        _http = SenderUtil.CreateClient(senderTimeoutMs, handler);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{tenant.MoEngageAppId}:{tenant.MoEngageApiKey}")));
    }

    public string AppId => _tenant.AppId;
    public string Destination => "moengage";

    public async Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
    {
        var customerId = CustomerId(item);
        if (customerId is null) return SenderUtil.MissingIdentity(item, "no_user_id");

        // SPEC §6.2 R3: rename property keys before writing attributes.
        using var properties = JsonDocument.Parse(
            _plan.ResolvePropertiesJson(item.EventName, "moengage", item.PropertiesJson));
        var platform = ResolvePlatform(item);

        var payload = SenderUtil.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "event");
            writer.WriteString("customer_id", customerId);
            writer.WriteStartArray("actions");
            writer.WriteStartObject();
            writer.WriteString("action", _plan.ResolveEventName(item.EventName, "moengage"));
            writer.WritePropertyName("attributes");
            properties.RootElement.WriteTo(writer);
            if (platform is not null) writer.WriteString("platform", platform);
            writer.WriteNumber("current_time",
                new DateTimeOffset(item.OccurredAt, TimeSpan.Zero).ToUnixTimeSeconds());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        try
        {
            using var response = await _http.PostAsync(
                $"{_tenant.MoEngageEndpoint}/v1/event/{Uri.EscapeDataString(_tenant.MoEngageAppId)}",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            if (response.IsSuccessStatusCode) return SendResult.Delivered();
            // MoEngage authenticates with basic auth built from the tenant
            // file, so a 401 here is a wrong key for every event, not one.
            return SenderUtil.MapStatus((int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SendResult.Retry($"network: {ex.Message}");
        }
    }

    /// <summary>
    /// Per-destination user_id: prefer identity's MoEngage-specific handle
    /// when the app set one via identify(), else fall back to the generic
    /// user_id — but never when the event names a different person than the
    /// session row does (see SenderUtil.WireUserId). Same key we send to
    /// MoEngage's /v1/customer/ endpoint from MoEngageCustomerSender must be
    /// used here so events attach to the right profile.
    ///
    /// A handle equal to the row's anonymous_id names the device, not a
    /// person: the Flutter SDK stamps it on pre-login registrations
    /// (EventPumpConfig.moengageDeviceCustomerId) so events from a session
    /// nobody has logged into reach the profile the app opened with
    /// MoEngage.setUniqueId. It is the last resort. Any user_id the event or
    /// the row names outranks it, so an event tracked after setUser() never
    /// lands on the device profile while the login's registration is still in
    /// flight.
    /// </summary>
    private static string? CustomerId(DeliveryItem item)
    {
        var identity = item.Identity;
        var handle = identity?.MoEngageCustomerId;
        if (identity is not null && SenderUtil.IsDeviceCustomerId(handle, identity.AnonymousId))
            return item.UserId ?? identity.UserId ?? handle;
        return SenderUtil.WireUserId(item.UserId, identity?.UserId, handle);
    }

    /// <summary>
    /// MoEngage's `platform` is a fixed enum — `ANDROID`, `iOS`, `web`, `TV`,
    /// `unknown` (Track Event API, verified 2026-10) — and platform-level
    /// Push and In-App campaigns match on it, so EP's own `app` / `backend`
    /// tags (EventValidation.DerivePlatform) never go on the wire. Only a
    /// value the event can vouch for is sent:
    ///
    /// - `web` when the event says so. A mobile browser's session row still
    ///   reads Android, and the event happened on the web.
    /// - `ANDROID` / `iOS` from the os of the session the event happened in:
    ///   app events, and backend events that name a session (tier-2 X-Event,
    ///   `first_visit`, emit_event with a session_key).
    /// - nothing otherwise. A person-resolved row's os is only that person's
    ///   most recent device, not where a backend event happened (SPEC §12).
    /// </summary>
    private static string? ResolvePlatform(DeliveryItem item)
    {
        using var eventContext = JsonDocument.Parse(item.ContextJson);
        if (SenderUtil.GetString(eventContext.RootElement, "platform") == "web") return "web";
        if (item.Identity is not { ResolvedByUserId: false, Os: { } os }) return null;
        if (os.Contains("android", StringComparison.OrdinalIgnoreCase)) return "ANDROID";
        if (os.Contains("ios", StringComparison.OrdinalIgnoreCase)) return "iOS";
        return null;
    }
}
